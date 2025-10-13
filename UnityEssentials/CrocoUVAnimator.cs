using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable] public struct CrocoUV { public float x, y; }
[Serializable] public struct CrocoFrame { public CrocoUV[] uv; public int duration; }
[Serializable] public class CrocoAnim { public string name; public string uuid; public int tileset; public List<CrocoFrame> frame; public int[] custom; }
[Serializable] class CrocoWrap { public List<CrocoAnim> items; }

public class CrocoUVAnimator : MonoBehaviour
{
    // --- Public inputs (Inspector) ---
    [Header("Inputs")]
    public TextAsset UvJson;                 // Crocotile uvAnimation.txt
    public Renderer TargetRenderer;          // Auto-detected if empty
    public int AnimationIndex = 0;           // Choose which animation
    public int FramesPerSecond = 12;         // Used when all durations=1

    public enum ApplyMode { MeshUV, MaterialOffset }
    public ApplyMode Mode = ApplyMode.MeshUV;

    [Header("Axis / Origin Fix")]
    public bool SwapXY = false;              // Swap U<->V if needed
    public bool InvertU = false;             // Flip U axis
    public bool InvertV = false;              // Crocotile top-left -> Unity bottom-left
    public float EpsilonShrink = 0.0f;       // 0..~0.01 to avoid bleeding

    [Header("Debug")]
    public bool PrintTileIndex = false;      // Logs tile (col,row) each frame assuming 1/N grid

    // --- Runtime ---
    public string TextureProperty = "_MainTex";  // Resolved automatically
    List<Rect> framesRect = new();
    float timer = 0f;
    int frameIndex = 0;
    Material runtimeMat;

    // Mesh-UV mode state
    MeshFilter meshFilter;
    Mesh runtimeMesh;
    List<int> idxLL = new(), idxUL = new(), idxUR = new(), idxLR = new();

    void Awake()
    {
        // Try to find a renderer on self or children
        if (!TargetRenderer) TargetRenderer = GetComponentInChildren<Renderer>(true);
        if (!TargetRenderer)
        {
            Debug.LogWarning("[CrocoUVAnimator] No Renderer found Å® falling back to MaterialOffset.");
            Mode = ApplyMode.MaterialOffset; // cannot animate without a renderer anyway
        }
        else
        {
            // Per-instance material
            runtimeMat = TargetRenderer.material;
            foreach (var p in new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColorTexture" })
                if (runtimeMat.HasProperty(p)) { TextureProperty = p; break; }
        }

        // Mesh-UV preparation (search in self/children)
        meshFilter = GetComponentInChildren<MeshFilter>(true);
        if (Mode == ApplyMode.MeshUV)
        {
            if (!meshFilter || !meshFilter.sharedMesh)
            {
                Debug.LogWarning("[CrocoUVAnimator] MeshUV mode requires a MeshFilter with a mesh Å® falling back to MaterialOffset.");
                Mode = ApplyMode.MaterialOffset;
            }
            else
            {
                runtimeMesh = Instantiate(meshFilter.sharedMesh);
                runtimeMesh.name = meshFilter.sharedMesh.name + " (RuntimeUV)";
                runtimeMesh.MarkDynamic();
                meshFilter.sharedMesh = runtimeMesh;
                BindQuadCorners(runtimeMesh);
                // If corners could not be bound, BindQuadCorners will flip Mode below in safety block
            }
        }

        RebuildFrames();
        if (framesRect.Count > 0) ApplyFrame(framesRect[0]);
    }

    void OnValidate()
    {
        // Avoid running during edit-time or when disabled
        if (!Application.isPlaying || !isActiveAndEnabled) return;

        RebuildFrames();
        frameIndex = 0; timer = 0f;
        if (framesRect.Count > 0) ApplyFrame(framesRect[0]);
    }

    void RebuildFrames()
    {
        framesRect.Clear();
        if (!UvJson) { Debug.LogWarning("[CrocoUVAnimator] UvJson is missing."); return; }

        // JsonUtility fix: wrap array + normalize key
        string json = UvJson.text.Replace("\"Animation Name\"", "\"name\"");
        string wrapped = "{\"items\":" + json + "}";
        var data = JsonUtility.FromJson<CrocoWrap>(wrapped);
        if (data?.items == null || data.items.Count == 0)
        {
            Debug.LogError("[CrocoUVAnimator] Parse failed or empty.");
            return;
        }

        AnimationIndex = Mathf.Clamp(AnimationIndex, 0, data.items.Count - 1);
        var anim = data.items[AnimationIndex];

        foreach (var fr in anim.frame)
        {
            Rect r = ToRect(fr.uv);
            int repeat = Mathf.Max(1, fr.duration);
            for (int i = 0; i < repeat; i++) framesRect.Add(r);
        }
    }

    Vector2 TransformUV(Vector2 p)
    {
        // Optional swap first
        if (SwapXY) p = new Vector2(p.y, p.x);
        if (InvertU) p.x = 1f - p.x;
        if (InvertV) p.y = 1f - p.y;
        return p;
    }

    Rect ToRect(CrocoUV[] quad)
    {
        float minx = 1f, miny = 1f, maxx = 0f, maxy = 0f;
        for (int i = 0; i < quad.Length; i++)
        {
            Vector2 p = TransformUV(new Vector2(quad[i].x, quad[i].y));
            if (p.x < minx) minx = p.x;
            if (p.y < miny) miny = p.y;
            if (p.x > maxx) maxx = p.x;
            if (p.y > maxy) maxy = p.y;
        }
        Vector2 size = new Vector2(maxx - minx, maxy - miny);
        Vector2 pos = new Vector2(minx, miny);

        if (EpsilonShrink > 0f)
        {
            pos += Vector2.one * (EpsilonShrink * 0.5f);
            size -= Vector2.one * EpsilonShrink;
        }
        return new Rect(pos, size);
    }

    void Update()
    {
        if (framesRect.Count == 0) return;
        float dur = 1f / Mathf.Max(1, FramesPerSecond);
        timer += Time.deltaTime;
        if (timer >= dur)
        {
            timer -= dur;
            frameIndex = (frameIndex + 1) % framesRect.Count;
            ApplyFrame(framesRect[frameIndex]);
        }
    }

    void ApplyFrame(Rect r)
    {
        // --- Safe fallback: if MeshUV is requested but mesh is invalid, switch to MaterialOffset.
        bool meshOk = (runtimeMesh != null);
        if (Mode == ApplyMode.MeshUV && !meshOk) Mode = ApplyMode.MaterialOffset;

        if (Mode == ApplyMode.MaterialOffset)
        {
            if (runtimeMat == null) return; // nothing to do
            runtimeMat.SetTextureScale(TextureProperty, r.size);
            runtimeMat.SetTextureOffset(TextureProperty, r.position);
        }
        else
        {
            // MeshUV mode: write 4 corners into UV array (with guards)
            var uvs = runtimeMesh.uv;
            if (uvs == null || uvs.Length == 0 ||
                (idxLL.Count + idxUL.Count + idxUR.Count + idxLR.Count) == 0)
            {
                // If corner binding failed, fallback silently
                if (runtimeMat != null)
                {
                    runtimeMat.SetTextureScale(TextureProperty, r.size);
                    runtimeMat.SetTextureOffset(TextureProperty, r.position);
                }
                return;
            }

            Vector2 ll = new Vector2(r.xMin, r.yMin);
            Vector2 ul = new Vector2(r.xMin, r.yMax);
            Vector2 ur = new Vector2(r.xMax, r.yMax);
            Vector2 lr = new Vector2(r.xMax, r.yMin);

            for (int i = 0; i < idxLL.Count; i++) uvs[idxLL[i]] = ll;
            for (int i = 0; i < idxUL.Count; i++) uvs[idxUL[i]] = ul;
            for (int i = 0; i < idxUR.Count; i++) uvs[idxUR[i]] = ur;
            for (int i = 0; i < idxLR.Count; i++) uvs[idxLR[i]] = lr;

            runtimeMesh.uv = uvs;
        }

        if (PrintTileIndex)
        {
            int N = 16; // guess from 0.0625 steps; make it a field if needed
            int col = Mathf.RoundToInt(r.xMin * N);
            int rowBottom = Mathf.RoundToInt(r.yMin * N);
            int rowTop = (N - 1) - rowBottom; // top-origin index
            Debug.Log($"[CrocoUVAnimator] col={col}, row(bottom)={rowBottom}, row(top)={rowTop}");
        }
    }

    // --- Bind quad corners of current mesh (groups indices by LL/UL/UR/LR) ---
    void BindQuadCorners(Mesh m)
    {
        idxLL.Clear(); idxUL.Clear(); idxUR.Clear(); idxLR.Clear();

        var uvs = m.uv;
        if (uvs == null || uvs.Length == 0)
        {
            Debug.LogWarning("[CrocoUVAnimator] Mesh has no UVs. Falling back to MaterialOffset.");
            Mode = ApplyMode.MaterialOffset; return;
        }

        float uMin = 10f, vMin = 10f, uMax = -10f, vMax = -10f;
        for (int i = 0; i < uvs.Length; i++)
        {
            Vector2 p = uvs[i];
            if (p.x < uMin) uMin = p.x; if (p.x > uMax) uMax = p.x;
            if (p.y < vMin) vMin = p.y; if (p.y > vMax) vMax = p.y;
        }

        float eps = 1e-4f;
        for (int i = 0; i < uvs.Length; i++)
        {
            Vector2 p = uvs[i];
            bool loU = Mathf.Abs(p.x - uMin) <= eps;
            bool hiU = Mathf.Abs(p.x - uMax) <= eps;
            bool loV = Mathf.Abs(p.y - vMin) <= eps;
            bool hiV = Mathf.Abs(p.y - vMax) <= eps;

            if (loU && loV) idxLL.Add(i);
            else if (loU && hiV) idxUL.Add(i);
            else if (hiU && hiV) idxUR.Add(i);
            else if (hiU && loV) idxLR.Add(i);
        }

        if (idxLL.Count + idxUL.Count + idxUR.Count + idxLR.Count == 0)
        {
            Debug.LogWarning("[CrocoUVAnimator] Could not bind quad corners. Falling back to MaterialOffset.");
            Mode = ApplyMode.MaterialOffset;
        }
    }
}
