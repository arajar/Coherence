﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Controller to sync a Unity Camera with a Blender Viewport.
/// 
/// On render updates, this will provide the SyncManager with
/// new RGB24 pixel data to feed back to Blender.
/// </summary>
public class ViewportController : MonoBehaviour
{
    /// <summary>
    /// Identifier to match this viewport with a Blender viewport
    /// </summary>
    public int ID 
    { 
        get { return InteropData.id; } 
    }

    public InteropViewport InteropData { get; private set; }

    public int Width
    {
        get { return tex.width; }
    }

    public int Height
    {
        get { return tex.height; }
    }

    Camera cam;
    public Texture2D tex;
    RenderTexture rt;

    public SyncManager Sync { get; set; }

    private void OnEnable()
    {
        cam = GetComponent<Camera>();
        
        if (cam == null) 
        {
            cam = gameObject.AddComponent<Camera>();
        }
    }

    private void OnDisable()
    {
        if (tex != null)
        {
            Destroy(tex);
            tex = null;
        }

        if (rt != null)
        {
            cam.targetTexture = null;

            rt.Release();
            rt = null;
        }
    }

    private void Update()
    {
        // TODO: OnPostRender() doesn't work in URP. Alternatives?
        
        if (Sync.IsConnected)
        {
            Sync.PublishRenderTexture(this, CaptureRenderTexture);
        }
    }

    public void Resize(int width, int height)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Setup a RenderTexture to match the viewport and 
    /// match the Camera component to Blender's view
    /// </summary>
    /// <param name="camera"></param>
    private void UpdateCamera()
    {
        var width = InteropData.width;
        var height = InteropData.height;
        var camera = InteropData.camera;

        // Resize the render texture / target Texture2D to match the viewport
        if (rt == null || rt.width != width || rt.height != height)
        {
            Debug.LogWarning($"Realloc to {width} x {height}");

            Profiler.BeginSample("Resize Viewport RT");

            if (cam.targetTexture != null)
            {
                cam.targetTexture.Release();
            }

            rt = new RenderTexture(
                width, 
                height, 
                16, RenderTextureFormat.ARGB32
            );
            rt.Create();

            cam.targetTexture = rt;
            
            tex = new Texture2D(
                width,
                height, 
                TextureFormat.RGB24, 
                false
            );
            
            Profiler.EndSample();
        }

        // TODO: Camera view matching magic. 
        // This is the hard part :^)
        
        
        // Blender is z-up - swap z/y everywhere 
        // TODO: But they could also change the up axis manually...
     /*   InteropMatrix4x4 t = camera.matrix;
        
        Vector3 forward;
        forward.x = t.m02;
        forward.y = t.m22;
        forward.z = t.m12;
 
        Vector3 up;
        up.x = t.m01;
        up.y = t.m21;
        up.z = t.m11;
        
        transform.position = Vector3.zero;

        transform.rotation = Quaternion.LookRotation(forward, up);
        transform.Translate(new Vector3(t.m03, t.m13, t.m23), Space.Self);
     */
        
        var p = new Vector3(camera.position.x, camera.position.z, camera.position.y);
        var f = new Vector3(camera.forward.x, camera.forward.z, camera.forward.y);
        var u = new Vector3(camera.up.x, camera.up.z, camera.up.y);

        transform.position = p;
        transform.rotation = Quaternion.LookRotation(f, u);
        
        // horizontal field of view = 2 atan(0.5 width / focallength)

        // 2*atan(0.5 * 1586 / 50)

        // Here's what I know thus far (from trial and error)
        cam.usePhysicalProperties = true;
        cam.focalLength = camera.lens;
        cam.gateFit = Camera.GateFitMode.Fill;
        cam.sensorSize = new Vector2(72, 72); // Matched via trial and error.
        // TODO: Actual value from Blender somehow?
        
        /*
        transform.localScale = new Vector4(
            new Vector4(t.m00, t.m10, t.m30, t.m20).magnitude,
            new Vector4(t.m01, t.m11, t.m31, t.m21).magnitude,
            new Vector4(t.m02, t.m12, t.m32, t.m22).magnitude
        );
        

        // Fuck that shit, set matrix directly.
        var mat = new Matrix4x4(
            new Vector4(t.m00, t.m10, t.m20, t.m30),
            new Vector4(t.m01, t.m11, t.m21, t.m31),
            new Vector4(t.m02, t.m12, t.m22, t.m32),
            new Vector4(t.m03, t.m13, t.m23, t.m33)
        );*/
        /*
        var mat = new Matrix4x4(
            new Vector4(t.m00, t.m01, t.m02, t.m03),
            new Vector4(t.m10, t.m11, t.m12, t.m13),
            new Vector4(t.m20, t.m21, t.m22, t.m23),
            new Vector4(t.m30, t.m31, t.m32, t.m33)
        );

        Debug.Log(mat.ToString());
        Debug.Log(cam.worldToCameraMatrix.ToString());
        */
        // cam.worldToCameraMatrix = mat;
    }

    /// <summary>
    /// Return array of pixels in <c>RGB24</c> format.
    /// 
    /// This is only executed if we have room in the pixelProducer
    /// buffer in the SyncManager
    /// </summary>
    /// <returns></returns>
    public byte[] CaptureRenderTexture()
    {
        Profiler.BeginSample("Copy Viewport RT to CPU");

        var prevRT = RenderTexture.active;
        var rt = cam.targetTexture;
        
        RenderTexture.active = cam.targetTexture;

        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply(); // TODO: Necessary?

        var data = tex.GetRawTextureData();

        RenderTexture.active = prevRT;
        
        Profiler.EndSample();
        
        return data;
    }

    /// <summary>
    /// Add/remove visible objects from this viewport camera
    /// </summary>
    internal void SetVisibleObjects(int[] visibleObjectIds)
    {
        // TODO: Run through objects and read their .visible flag
        // for this viewport. Do some Unity magic to make them visible/invisible
        // to this particular camera when rendering.
    }

    internal void UpdateFromInterop(InteropViewport viewport)
    {
        InteropData = viewport;
        UpdateCamera();
    }
}