﻿using SharedMemory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;

public delegate int MessageHandler(string target, IntPtr ptr);

/// <summary>
/// Manage shared memory buffers and messages between Blender and Unity
/// </summary>
class Bridge : IDisposable
{
    // Let's assume a fixed size for now. Will make it more intelligent later.
    // But since we're splitting out scene objects into multiple messages now instead
    // of a single message with everything - the size is now a restriction of chunk data.
    // It is also technically a restriction to any array-based messages, such as visible
    // objects in a viewport and mesh data (until message splitting is implemented)
    const int MAX_MESSAGE_SIZE = 1 * 1024 * 1024;

    const string VIEWPORT_IMAGE_BUFFER_ID = "UnityViewportImage";

    const string MESSAGE_PRODUCER_ID = "BlenderMessages";
    const string MESSAGE_CONSUMER_ID = "UnityMessages";
    
    public bool IsConnected { get; private set; }

    public Scene Scene { get; private set; }

    /// <summary>
    /// How long to wait for a readable node to become available
    /// </summary>
    const int READ_WAIT = 10;

    Dictionary<int, Viewport> Viewports { get; set; }

    InteropUnityState unityState;

    CircularBuffer pixelsConsumer;
    
    InteropMessenger messages;

    public Dictionary<RpcRequest, MessageHandler> handlers;

    public Bridge()
    {
        Scene = new Scene();
        Viewports = new Dictionary<int, Viewport>();
        handlers = new Dictionary<RpcRequest, MessageHandler>();
    }

    ~Bridge()
    {
        Dispose();
    }

    public void Dispose()
    {
        pixelsConsumer?.Dispose();
        pixelsConsumer = null;

        messages?.Dispose();
        messages = null;

        foreach (var viewport in Viewports)
        {
            viewport.Value.Dispose();
        }

        Viewports.Clear();
    }

    #region Unity IO Management

    /// <summary>
    /// Initialize a shared memory space between Blender and Unity
    /// </summary>
    public void Start()
    {
        // Header + 3 bytes per pixel.
        // This caps out at a specific viewport texture size because we need
        // a fixed buffer size to allocate - even if the individual viewport
        // images coming back from Unity are smaller.
        var size = FastStructure.SizeOf<InteropRenderHeader>() + (
            Viewport.MAX_VIEWPORT_WIDTH * Viewport.MAX_VIEWPORT_HEIGHT * 3
        );

        // Buffer for render data coming from Unity (consume-only)
        pixelsConsumer = new CircularBuffer(VIEWPORT_IMAGE_BUFFER_ID, 2, size);

        // Two-way channel between Blender and Unity
        messages = new InteropMessenger();
        messages.ConnectAsMaster(MESSAGE_CONSUMER_ID, MESSAGE_PRODUCER_ID, MAX_MESSAGE_SIZE);

        // Message handlers 
        handlers[RpcRequest.Connect] = OnConnect;
        handlers[RpcRequest.Disconnect] = OnDisconnect;
        handlers[RpcRequest.UpdateUnityState] = OnUpdateUnityState;
    }
    
    public void Shutdown()
    {
        IsConnected = false;
        messages?.WriteDisconnect();

        pixelsConsumer?.Dispose();
        pixelsConsumer = null;

        messages?.Dispose();
        messages = null;
    }
    
    /// <summary>
    /// Send queued messages and read new messages and render texture data from Unity
    /// </summary>
    internal void Update()
    {
        messages.ProcessQueue();
        ConsumeMessage();
    }

    /// <summary>
    /// Read from the viewport image buffer and copy 
    /// pixel data into the appropriate viewport.
    /// </summary>
    internal void ConsumePixels()
    {
        if (pixelsConsumer.ShuttingDown)
        {
            return;
        }

        pixelsConsumer.Read((ptr) =>
        {
            var headerSize = FastStructure.SizeOf<InteropRenderHeader>();
            var header = FastStructure.PtrToStructure<InteropRenderHeader>(ptr);

            if (!Viewports.ContainsKey(header.viewportId))
            {
                InteropLogger.Warning($"Got render texture for unknown viewport {header.viewportId}");
                return headerSize;
            }

            var viewport = Viewports[header.viewportId];
            var pixelDataSize = viewport.ReadPixelData(header, ptr + headerSize);
            
            return headerSize + pixelDataSize;
        }, READ_WAIT);
    }

    /// <summary>
    /// Consume a single message off the read queue. 
    /// </summary>
    internal void ConsumeMessage()
    {
        messages.Read((target, header, ptr) =>
        {
            if (!handlers.ContainsKey(header.type))
            {
                InteropLogger.Warning($"Unhandled request type {header.type} for {target}");
                return 0;
            }

            return handlers[header.type](target, ptr);
        });
    }
    
    /// <summary>
    /// Send an <see cref="RpcRequest"/> with target <see cref="IInteropSerializable{T}.Name"/> 
    /// of <paramref name="entity"/> and a <typeparamref name="T"/> payload.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="type"></param>
    /// <param name="entity"></param>
    internal void SendEntity<T>(RpcRequest type, IInteropSerializable<T> entity) where T : struct
    {
        if (!IsConnected)
        {
            return;
        }

        var data = entity.Serialize();
        messages.ReplaceOrQueue(type, entity.Name, ref data);
    }

    internal void SendArray<T>(RpcRequest type, string target, T[] data, bool allowSplitMessages) where T : struct
    {
        if (!IsConnected)
        {
            return;
        }
        
        // TODO: Zero length array support. Makes sense in some use cases
        // but not others (e.g. don't send RpcRequest.UpdateUVs if there 
        // are no UVs to send)
        if (data == null || data.Length < 1)
        {
            return;
        }

        if (messages.ReplaceOrQueueArray(type, target, data, allowSplitMessages))
        {
            InteropLogger.Debug($"Replaced queued {type} for {target}");
        }
    }

    /// <summary>
    /// Send <b>all</b> available mesh data (vertices, triangles, normals, UVs, etc) to Unity
    /// </summary>
    /// <param name="obj"></param>
    internal void SendAllMeshData(SceneObject obj)
    {
        if (!IsConnected)
        {
            return;
        }
        
        SendArray(RpcRequest.UpdateVertices, obj.Name, obj.Vertices, true);
        SendArray(RpcRequest.UpdateTrianges, obj.Name, obj.Triangles, true);
        SendArray(RpcRequest.UpdateNormals, obj.Name, obj.Normals, true);
        SendArray(RpcRequest.UpdateUVs, obj.Name, obj.GetUV(0), true);
        // ... and so on, per-buffer ...
    }

    #endregion 

    #region Unity Message Handlers
    
    private int OnConnect(string target, IntPtr ptr)
    {
        unityState = FastStructure.PtrToStructure<InteropUnityState>(ptr);
        IsConnected = true;

        InteropLogger.Debug($"{target} - {unityState.version} connected. Flavor Blasting.");
        
        // Send the scene + all current object metadata
        SendEntity(RpcRequest.UpdateScene, Scene);

        foreach (var obj in Scene.Objects)
        {
            SendEntity(RpcRequest.AddObjectToScene, obj.Value);
        }

        // Send active viewports and their visibility liists
        foreach (var vp in Viewports)
        {
            var viewport = vp.Value;

            SendEntity(RpcRequest.AddViewport, viewport);
            SendArray(
                RpcRequest.UpdateVisibleObjects, 
                viewport.Name, 
                viewport.VisibleObjectIds, 
                false
            );
        }

        // THEN send current mesh data for all objects with meshes.
        // We do this last so that we can ensure Unity is completely setup and 
        // ready to start accepting large data chunks.
        foreach (var entry in Scene.Objects)
        {
            SendAllMeshData(entry.Value);
        }

        return FastStructure.SizeOf<InteropUnityState>();
    }

    private int OnDisconnect(string target, IntPtr ptr)
    {
        IsConnected = false;
        messages.ClearQueue();

        InteropLogger.Debug("Unity disconnected");
        return 0;
    }

    private int OnUpdateUnityState(string target, IntPtr ptr)
    {
        unityState = FastStructure.PtrToStructure<InteropUnityState>(ptr);
        IsConnected = true;

        InteropLogger.Debug($"Unity state update");
        
        return FastStructure.SizeOf<InteropUnityState>();
    }

    #endregion

    #region Viewport Management

    public Viewport GetViewport(int id)
    {
        if (!HasViewport(id))
        {
            throw new Exception($"Viewport {id} does not exist");
        }

        return Viewports[id];
    }

    public bool HasViewport(int id)
    {
        return Viewports.ContainsKey(id);
    }

    /// <summary>
    /// Add a new viewport to our state and notify Unity
    /// </summary>
    /// <param name="id"></param>
    /// <param name="initialWidth"></param>
    /// <param name="initialHeight"></param>
    internal void AddViewport(int id, int initialWidth, int initialHeight)
    {
        if (HasViewport(id))
        {
            throw new Exception($"Viewport {id} already exists");
        }

        var viewport = new Viewport(id, initialWidth, initialHeight);

        // Assume everything is visible upon creation
        viewport.SetVisibleObjects(Scene.GetObjectIds());
        Viewports[id] = viewport;

        SendEntity(RpcRequest.AddViewport, viewport);
        SendArray(RpcRequest.UpdateVisibleObjects, viewport.Name, viewport.VisibleObjectIds, false);
    }

    /// <summary>
    /// Remove a viewport from our state and notify Unity
    /// </summary>
    /// <param name="id"></param>
    internal void RemoveViewport(int id)
    {
        var viewport = Viewports[id];
        
        SendEntity(RpcRequest.RemoveViewport, viewport);
        
        Viewports.Remove(id);
        viewport.Dispose();
    }

    #endregion
}