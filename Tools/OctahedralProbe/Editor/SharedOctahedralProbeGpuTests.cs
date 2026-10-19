using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition.Experimental;
using Debug = UnityEngine.Debug;

// Standalone GPU integration tests. No NUnit or HDRP package dependency is required.
public static class SharedOctahedralProbeGpuTests
{
    [Serializable] sealed class CaseResult { public string name; public bool passed; public double seconds; public string detail; }
    [Serializable] sealed class Report
    {
        public string editor, gpu, api;
        public int passed, failed;
        public List<CaseResult> cases = new List<CaseResult>();
    }
    static readonly Vector3[] kU = { Vector3.back, Vector3.forward, Vector3.right, Vector3.right, Vector3.right, Vector3.left };
    static readonly Vector3[] kV = { Vector3.down, Vector3.down, Vector3.forward, Vector3.back, Vector3.down, Vector3.down };
    static ComputeShader s_Shader;
    static string s_Output;
    static readonly Report s_Report = new Report();
    const int kFaceSize = 64, kTileSize = 64;

    public static void Run()
    {
        var arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, "-octTestOutput");
        s_Output = index >= 0 ? arguments[index + 1] : Path.GetFullPath("OctProbeTestResults");
        Directory.CreateDirectory(s_Output);
        s_Report.editor = Application.unityVersion;
        s_Report.gpu = SystemInfo.graphicsDeviceName;
        s_Report.api = SystemInfo.graphicsDeviceType.ToString();
        try
        {
            Require(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A real GPU is required; do not use -nographics.");
            s_Shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/SharedOctahedralProbe/SharedOctahedralProbe.compute");
            Require(s_Shader != null, "Compute shader was not imported.");
            RunCase("shared cube is reused and probes retain independent HDR results", SharedCubeIsolation);
            RunCase("GGX preserves a constant environment at every published mip", ConstantAllMips);
            RunCase("cube reprojection preserves an analytic direction field including borders", CubeDirectionField);
            RunCase("six single faces agree with the cube reference", FaceDirectionField);
            RunCase("GGX from raw Oct agrees with GGX from cube", FaceFilteredField);
            RunCase("incomplete face updates preserve the previously published result", PartialPublication);
            RunCase("workspace rejects overlapping and foreign captures", WorkspaceOwnership);
            RunCase("failed capture does not publish or lock the workspace", FailedCapture);
            RunCase("removed slots can be reused without altering another probe", SlotReuse);
            RunCase("fractional LOD and Oct seams do not bleed adjacent probes", FractionalLodAndSeams);
            RunCase("fractional LOD uses the correct UV for each mip", FractionalLodCoordinates);
            RunCase("roughness prefilter reduces high frequency environment variance", RoughnessVariance);
            RunCase("all six sequential camera faces match camera RenderToCubemap", CameraFaceCapture);
            RunCase("disposing releases resources and rejects further work", Dispose);
            RunCase("constructor validates unsupported layouts", ValidateArguments);
        }
        catch (Exception error)
        {
            ++s_Report.failed;
            s_Report.cases.Add(new CaseResult { name = "test harness", detail = error.ToString() });
        }
        File.WriteAllText(Path.Combine(s_Output, "results.json"), JsonUtility.ToJson(s_Report, true));
        Debug.Log($"OCT_PROBE_TESTS: {s_Report.passed} passed, {s_Report.failed} failed; GPU={s_Report.gpu}; results={s_Output}");
        EditorApplication.Exit(s_Report.failed == 0 ? 0 : 1);
    }

    static void RunCase(string name, Action action)
    {
        var result = new CaseResult { name = name };
        var timer = Stopwatch.StartNew();
        var gpuErrors = new List<string>();
        Application.LogCallback log = (text, trace, type) =>
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) gpuErrors.Add(text);
        };
        Application.logMessageReceived += log;
        try
        {
            action();
            Require(gpuErrors.Count == 0, string.Join("\n", gpuErrors));
            result.passed = true;
            ++s_Report.passed;
        }
        catch (Exception error) { result.detail = error.ToString(); ++s_Report.failed; }
        finally { Application.logMessageReceived -= log; result.seconds = timer.Elapsed.TotalSeconds; }
        s_Report.cases.Add(result);
        Debug.Log((result.passed ? "PASS " : "FAIL ") + name + " " + result.detail);
    }

    static SharedOctahedralProbeAtlas NewAtlas(int rows = 2) => new SharedOctahedralProbeAtlas(s_Shader, kFaceSize, kTileSize, rows, 256);
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    static void Fill(RenderTexture cube, Color color)
    {
        var previous = RenderTexture.active;
        try
        {
            for (int face = 0; face < 6; ++face)
            {
                Graphics.SetRenderTarget(cube, 0, (CubemapFace)face);
                GL.Clear(false, true, color);
            }
        }
        finally { RenderTexture.active = previous; }
    }

    static Color[] Read(RenderTexture texture, int mip = 0)
    {
        var previous = RenderTexture.active;
        int size = texture.width >> mip;
        var cpu = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
        try
        {
            Graphics.SetRenderTarget(texture, mip);
            cpu.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            cpu.Apply(false);
            return cpu.GetPixels(); // Synchronous readback also orders all preceding GPU work.
        }
        finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(cpu); }
    }

    static float Error(Color a, Color b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));
    static Color DirectionColor(Vector3 d) => new Color(d.x * 0.5f + 0.5f, d.y * 0.5f + 0.5f, d.z * 0.5f + 0.5f, 1);
    static Vector3 FaceDirection(int face, float u, float v) => (Vector3.Cross(kV[face], kU[face]) + (u * 2 - 1) * kU[face] + (v * 2 - 1) * kV[face]).normalized;

    static Color[] FacePixels(int face, Func<Vector3, Color> evaluate)
    {
        var pixels = new Color[kFaceSize * kFaceSize];
        for (int y = 0; y < kFaceSize; ++y)
            for (int x = 0; x < kFaceSize; ++x)
                pixels[y * kFaceSize + x] = evaluate(FaceDirection(face, (x + 0.5f) / kFaceSize, (y + 0.5f) / kFaceSize));
        return pixels;
    }

    static void CaptureAnalyticCube(SharedOctahedralProbeAtlas atlas, int id, Func<Vector3, Color> evaluate)
    {
        var source = new Cubemap(kFaceSize, TextureFormat.RGBAHalf, false);
        try
        {
            for (int face = 0; face < 6; ++face) source.SetPixels(FacePixels(face, evaluate), (CubemapFace)face);
            source.Apply(false, false);
            atlas.CaptureCubemap(id, 1, target =>
            {
                for (int face = 0; face < 6; ++face) Graphics.CopyTexture(source, face, 0, target, face, 0);
                return true;
            });
        }
        finally { UnityEngine.Object.DestroyImmediate(source); }
    }

    static void CaptureAnalyticFaces(SharedOctahedralProbeAtlas atlas, int id, Func<Vector3, Color> evaluate)
    {
        var source = new Texture2D(kFaceSize, kFaceSize, TextureFormat.RGBAHalf, false, true);
        try
        {
            atlas.BeginFaces(id);
            // Deliberately use a nonstandard order to expose overwrite/mask bugs.
            foreach (int face in new[] { 5, 0, 3, 1, 4, 2 })
            {
                source.SetPixels(FacePixels(face, evaluate));
                source.Apply(false, false);
                atlas.CaptureFace(id, (CubemapFace)face, (target, _) => Graphics.CopyTexture(source, 0, 0, target, 0, 0));
            }
            atlas.CompleteFaces(id, 1);
        }
        finally { UnityEngine.Object.DestroyImmediate(source); }
    }

    static void SharedCubeIsolation()
    {
        using var atlas = NewAtlas();
        int sharedId = 0;
        atlas.CaptureCubemap(10, 7, target => { sharedId = target.GetInstanceID(); Fill(target, new Color(2, 0, 0)); return true; });
        atlas.CaptureCubemap(20, 9, target => { Require(sharedId == target.GetInstanceID(), "Capture workspace was not reused."); Fill(target, Color.blue); return true; });
        Require(atlas.TryGetProbe(10, out var a, out var av) && av == 7, "First probe/version lost.");
        Require(atlas.TryGetProbe(20, out var b, out var bv) && bv == 9 && a != b, "Probe IDs alias one another.");
        var pixels = Read(atlas.texture);
        Require(Error(pixels[32 * 128 + 32], new Color(2, 0, 0)) < 0.002f, "First probe overwritten or HDR clipped.");
        Require(Error(pixels[32 * 128 + 96], Color.blue) < 0.002f, "Second probe is wrong.");
    }

    static void ConstantAllMips()
    {
        using var atlas = NewAtlas(1);
        Color expected = new Color(0.25f, 1.5f, 3.0f, 1);
        atlas.CaptureCubemap(1, 1, target => { Fill(target, expected); return true; });
        for (int mip = 0; mip <= atlas.maxMip; ++mip)
            foreach (Color actual in Read(atlas.texture, mip)) Require(Error(actual, expected) < 0.004f, "Constant energy changed at mip " + mip);
    }

    static Vector2 Repeat(Vector2 uv)
    {
        float u = uv.x, v = uv.y;
        if (u < 0) return v < 0 ? new Vector2(1 + u, 1 + v) : v < 1 ? new Vector2(-u, 1 - v) : new Vector2(1 + u, v - 1);
        if (u < 1) return v < 0 ? new Vector2(1 - u, -v) : v < 1 ? uv : new Vector2(1 - u, 2 - v);
        return v < 0 ? new Vector2(u - 1, 1 + v) : v < 1 ? new Vector2(2 - u, 1 - v) : new Vector2(u - 1, v - 1);
    }

    static Vector3 PixelDirection(int x, int y)
    {
        Vector2 f = Repeat(new Vector2((x - 0.5f) / (kTileSize - 2), (y - 0.5f) / (kTileSize - 2))) * 2 - Vector2.one;
        Vector3 n = new Vector3(f.x, f.y, 1 - Mathf.Abs(f.x) - Mathf.Abs(f.y));
        float t = Mathf.Max(-n.z, 0);
        n.x += n.x >= 0 ? -t : t;
        n.y += n.y >= 0 ? -t : t;
        return n.normalized;
    }

    static void CubeDirectionField()
    {
        using var atlas = NewAtlas(1);
        CaptureAnalyticCube(atlas, 1, DirectionColor);
        Color[] pixels = Read(atlas.texture);
        float maximum = 0;
        for (int y = 0; y < kTileSize; ++y)
            for (int x = 0; x < kTileSize; ++x)
                maximum = Mathf.Max(maximum, Error(pixels[y * kTileSize + x], DirectionColor(PixelDirection(x, y))));
        Require(maximum < 0.02f, "Direction/border max error: " + maximum);
        Save("cube-direction.png", pixels, kTileSize);
    }

    static void FaceDirectionField()
    {
        using var reference = NewAtlas(1);
        using var progressive = NewAtlas(1);
        CaptureAnalyticCube(reference, 1, DirectionColor);
        CaptureAnalyticFaces(progressive, 1, DirectionColor);
        Require(!progressive.hasSharedCubemap, "Single-face path allocated a Cubemap.");
        Compare(Read(reference.texture), Read(progressive.texture), 0.025f, "Single-face reprojection");
        Save("face-direction.png", Read(progressive.texture), kTileSize);
    }

    static void FaceFilteredField()
    {
        using var reference = NewAtlas(1);
        using var progressive = NewAtlas(1);
        CaptureAnalyticCube(reference, 1, DirectionColor);
        CaptureAnalyticFaces(progressive, 1, DirectionColor);
        for (int mip = 1; mip <= reference.maxMip; ++mip)
            Compare(Read(reference.texture, mip), Read(progressive.texture, mip), 0.035f, "GGX mip " + mip);
    }

    static void Compare(Color[] a, Color[] b, float tolerance, string context)
    {
        Require(a.Length == b.Length, "Size mismatch.");
        float maximum = 0;
        for (int i = 0; i < a.Length; ++i) maximum = Mathf.Max(maximum, Error(a[i], b[i]));
        Require(maximum < tolerance, context + " max error=" + maximum + ", tolerance=" + tolerance);
    }

    static void PartialPublication()
    {
        using var atlas = NewAtlas(1);
        atlas.CaptureCubemap(1, 7, target => { Fill(target, Color.red); return true; });
        atlas.BeginFaces(1);
        for (int face = 0; face < 5; ++face)
            atlas.CaptureFace(1, (CubemapFace)face, (target, _) => { var previous = RenderTexture.active; RenderTexture.active = target; GL.Clear(false, true, Color.blue); RenderTexture.active = previous; });
        Throws<InvalidOperationException>(() => atlas.CompleteFaces(1, 8));
        Require(atlas.TryGetProbe(1, out _, out uint version) && version == 7, "Partial update was published.");
        foreach (var pixel in Read(atlas.texture)) Require(Error(pixel, Color.red) < 0.002f, "Partial capture altered published data.");
        atlas.CancelFaces(1);
        atlas.BeginFaces(1);
        Throws<InvalidOperationException>(() => atlas.CompleteFaces(1, 9)); // Mask must reset after cancellation.
        atlas.CancelFaces(1);
    }

    static void WorkspaceOwnership()
    {
        using var atlas = NewAtlas();
        atlas.BeginFaces(1);
        Require(!atlas.TryGetProbe(1, out _, out _), "An uninitialized probe is visible.");
        Throws<InvalidOperationException>(() => atlas.BeginFaces(2));
        Throws<InvalidOperationException>(() => atlas.CaptureCubemap(2, 1, _ => true));
        Throws<InvalidOperationException>(() => atlas.CaptureFace(2, CubemapFace.PositiveX, (_, __) => { }));
        Throws<ArgumentOutOfRangeException>(() => atlas.CaptureFace(1, CubemapFace.Unknown, (_, __) => { }));
        atlas.CancelFaces(1);
    }

    static void FailedCapture()
    {
        using var atlas = NewAtlas();
        Require(!atlas.CaptureCubemap(1, 1, _ => false), "Failed callback reported success.");
        Require(!atlas.TryGetProbe(1, out _, out _), "Failed capture was published.");
        Throws<Exception>(() => atlas.CaptureCubemap(1, 1, _ => throw new Exception("Expected callback failure")));
        atlas.CaptureCubemap(1, 2, target => { Fill(target, Color.green); return true; });
        Require(atlas.TryGetProbe(1, out _, out uint version) && version == 2, "Workspace stayed locked after failure.");
    }

    static void SlotReuse()
    {
        using var atlas = NewAtlas();
        for (int i = 0; i < 4; ++i) atlas.CaptureCubemap(i, 1, target => { Fill(target, Color.red); return true; });
        Throws<InvalidOperationException>(() => atlas.CaptureCubemap(5, 1, _ => true));
        atlas.TryGetProbe(1, out var oldRegion, out _);
        atlas.RemoveProbe(1);
        atlas.CaptureCubemap(5, 1, target => { Fill(target, Color.blue); return true; });
        Require(!atlas.TryGetProbe(1, out _, out _), "Removed probe is still visible.");
        Require(atlas.TryGetProbe(5, out var newRegion, out _) && oldRegion == newRegion, "Slot was not reused.");
        Require(Error(Read(atlas.texture)[32 * 128 + 32], Color.red) < 0.002f, "Another probe was altered.");
    }

    static Color[] Sample(SharedOctahedralProbeAtlas atlas, int probe, Vector4[] queries)
    {
        Require(atlas.TryGetProbe(probe, out var region, out _), "Probe missing.");
        int kernel = s_Shader.FindKernel("SampleAtlas");
        using var input = new ComputeBuffer(queries.Length, 16);
        using var output = new ComputeBuffer(queries.Length, 16);
        input.SetData(queries);
        s_Shader.SetInt("_QueryCount", queries.Length);
        s_Shader.SetFloat("_MaxMip", atlas.maxMip);
        s_Shader.SetFloat("_AtlasSize", atlas.texture.width);
        s_Shader.SetVector("_ProbeScaleOffset", region);
        s_Shader.SetTexture(kernel, "_AtlasTexture", atlas.texture);
        s_Shader.SetBuffer(kernel, "_Queries", input);
        s_Shader.SetBuffer(kernel, "_Samples", output);
        s_Shader.Dispatch(kernel, (queries.Length + 63) / 64, 1, 1);
        var pixels = new Color[queries.Length];
        output.GetData(pixels);
        return pixels;
    }

    static void FractionalLodAndSeams()
    {
        using var atlas = NewAtlas();
        atlas.CaptureCubemap(1, 1, target => { Fill(target, Color.red); return true; });
        atlas.CaptureCubemap(2, 1, target => { Fill(target, Color.blue); return true; });
        var queries = new List<Vector4>();
        foreach (float lod in new[] { -1f, 0f, 0.3f, 1.7f, 3.5f, 4f, 99f })
            foreach (var direction in new[] { Vector3.back, new Vector3(0.001f, 1, -1), new Vector3(-0.001f, 1, -1), new Vector3(0.001f, -1, -1), new Vector3(1, 0.001f, -1), new Vector3(1, -0.001f, -1) })
                queries.Add(new Vector4(direction.x, direction.y, direction.z, lod));
        foreach (Color pixel in Sample(atlas, 1, queries.ToArray())) Require(Error(pixel, Color.red) < 0.003f, "Adjacent probe bled into Oct boundary.");
    }

    static Color CpuBilinear(Color[] pixels, int size, float u, float v)
    {
        float x = u * size - 0.5f, y = v * size - 0.5f;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        Func<int, int, Color> at = (a, b) => pixels[Mathf.Clamp(b, 0, size - 1) * size + Mathf.Clamp(a, 0, size - 1)];
        return Color.Lerp(Color.Lerp(at(x0, y0), at(x0 + 1, y0), x - x0), Color.Lerp(at(x0, y0 + 1), at(x0 + 1, y0 + 1), x - x0), y - y0);
    }

    static void FractionalLodCoordinates()
    {
        using var atlas = NewAtlas(1);
        CaptureAnalyticCube(atlas, 1, DirectionColor);
        // +Z encodes to (0.5,0.5); +X to (1,0.5), which exercises the border.
        foreach (Vector3 direction in new[] { Vector3.forward, Vector3.right })
        {
            float u = direction == Vector3.right ? 1 : 0.5f;
            Color a = CpuBilinear(Read(atlas.texture, 1), 32, (u * 30 + 1) / 32, 0.5f);
            Color b = CpuBilinear(Read(atlas.texture, 2), 16, (u * 14 + 1) / 16, 0.5f);
            var actual = Sample(atlas, 1, new[] { new Vector4(direction.x, direction.y, direction.z, 1.3f) })[0];
            Require(Error(actual, Color.Lerp(a, b, 0.3f)) < 0.003f, "Incorrect fractional-LOD border coordinates.");
        }
    }

    static void RoughnessVariance()
    {
        using var atlas = NewAtlas(1);
        CaptureAnalyticCube(atlas, 1, d =>
        {
            float value = Mathf.Sin(d.x * 37) * Mathf.Sin(d.y * 37) * Mathf.Sin(d.z * 37) > 0 ? 1 : 0;
            return new Color(value, value, value, 1);
        });
        Func<Color[], double> variance = pixels =>
        {
            double sum = 0, square = 0;
            foreach (Color pixel in pixels) { sum += pixel.r; square += pixel.r * pixel.r; }
            return square / pixels.Length - Math.Pow(sum / pixels.Length, 2);
        };
        double smooth = variance(Read(atlas.texture));
        double rough = variance(Read(atlas.texture, 3));
        Require(smooth > 0.05 && rough < smooth * 0.3, "GGX did not reduce variance: " + smooth + " -> " + rough);
        Save("roughness-mip0.png", Read(atlas.texture), kTileSize);
        Save("roughness-mip3.png", Read(atlas.texture, 3), kTileSize >> 3);
    }

    static void CameraFaceCapture()
    {
        // Render colored room walls with actual camera views, not only synthetic uploads.
        var root = new GameObject("Oct test room");
        var cameraObject = new GameObject("Oct test camera");
        var materials = new List<Material>();
        var colors = new[] { Color.red, Color.green, Color.blue, Color.yellow, Color.magenta, Color.cyan };
        var directions = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        var ups = new[] { Vector3.up, Vector3.up, Vector3.back, Vector3.forward, Vector3.up, Vector3.up };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 30;
        camera.fieldOfView = 90;
        camera.aspect = 1;
        camera.allowHDR = true;
        try
        {
            for (int i = 0; i < 6; ++i)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
                wall.transform.SetParent(root.transform);
                wall.transform.position = directions[i] * 10;
                wall.transform.rotation = Quaternion.LookRotation(directions[i]); // Quad front faces its local -Z.
                wall.transform.localScale = Vector3.one * 25;
                var material = new Material(Shader.Find("Unlit/Color"));
                material.color = colors[i];
                materials.Add(material);
                wall.GetComponent<Renderer>().sharedMaterial = material;
                // An asymmetric patch detects within-face X/Y flips that solid-colored walls cannot expose.
                var patch = GameObject.CreatePrimitive(PrimitiveType.Quad);
                patch.transform.SetParent(root.transform);
                patch.transform.position = directions[i] * 9 + kU[i] * 3 + kV[i] * 4;
                patch.transform.rotation = wall.transform.rotation;
                patch.transform.localScale = Vector3.one * 2;
                var patchMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.white };
                materials.Add(patchMaterial);
                patch.GetComponent<Renderer>().sharedMaterial = patchMaterial;
            }
            using var cube = NewAtlas(1);
            using var face = NewAtlas(1);
            Require(cube.CaptureCubemap(1, 1, target => camera.RenderToCubemap(target)), "Camera.RenderToCubemap failed.");
            face.BeginFaces(1);
            for (int i = 0; i < 6; ++i)
                face.CaptureFace(1, (CubemapFace)i, (target, f) =>
                {
                    camera.transform.rotation = Quaternion.LookRotation(directions[(int)f], ups[(int)f]);
                    camera.targetTexture = target;
                    camera.Render();
                    camera.targetTexture = null;
                });
            face.CompleteFaces(1, 1);
            // Borders between differently colored faces are discontinuous; compare away from those boundaries.
            Color[] a = Read(cube.texture), b = Read(face.texture);
            int checkedPixels = 0;
            for (int y = 1; y < 63; ++y)
                for (int x = 1; x < 63; ++x)
                {
                    Vector3 d = PixelDirection(x, y);
                    float[] magnitudes = { Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z) };
                    Array.Sort(magnitudes);
                    if (magnitudes[2] - magnitudes[1] < 0.1f) continue;
                    Require(Error(a[y * 64 + x], b[y * 64 + x]) < 0.025f, "Camera face orientation mismatch at " + x + "," + y);
                    ++checkedPixels;
                }
            Require(checkedPixels > 1000, "Too few camera pixels were checked.");
            Require(Error(a[32 * 64 + 32], Color.magenta) < 0.03f, "Camera room did not render the expected +Z wall.");
            Save("camera-cube.png", a, 64);
            Save("camera-faces.png", b, 64);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            foreach (var material in materials) UnityEngine.Object.DestroyImmediate(material);
        }
    }

    static void Dispose()
    {
        var atlas = NewAtlas();
        atlas.BeginFaces(1);
        atlas.Dispose();
        atlas.Dispose();
        Require(atlas.texture == null, "Atlas texture retained after disposal.");
        Throws<ObjectDisposedException>(() => atlas.BeginFaces(2));
        Throws<ObjectDisposedException>(() => atlas.TryGetProbe(1, out _, out _));
    }

    static void ValidateArguments()
    {
        Throws<ArgumentNullException>(() => new SharedOctahedralProbeAtlas(null));
        Throws<ArgumentOutOfRangeException>(() => new SharedOctahedralProbeAtlas(s_Shader, faceSize: 63));
        Throws<ArgumentOutOfRangeException>(() => new SharedOctahedralProbeAtlas(s_Shader, tileSize: 2));
        Throws<ArgumentOutOfRangeException>(() => new SharedOctahedralProbeAtlas(s_Shader, tilesPerRow: 3));
        Throws<ArgumentOutOfRangeException>(() => new SharedOctahedralProbeAtlas(s_Shader, sampleCount: 0));
    }

    static void Save(string name, Color[] pixels, int size)
    {
        var image = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        image.SetPixels(pixels);
        image.Apply(false);
        File.WriteAllBytes(Path.Combine(s_Output, name), image.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(image);
    }
}
