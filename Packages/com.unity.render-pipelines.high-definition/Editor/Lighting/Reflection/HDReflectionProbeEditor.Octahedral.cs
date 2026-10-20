using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    sealed partial class HDReflectionProbeEditor
    {
        [MenuItem("CONTEXT/ReflectionProbe/Convert Assigned Cubemap to Raw Oct")]
        static void ConvertAssignedCubemapToOct(MenuCommand command)
        {
            var probe = ((ReflectionProbe)command.context).GetComponent<HDAdditionalReflectionData>();
            if (probe == null || probe.mode == ProbeSettings.Mode.Realtime || probe.texture == null ||
                probe.texture.dimension != TextureDimension.Cube)
            {
                Debug.LogError("Assign a baked/custom Cubemap before converting it to raw Oct.");
                return;
            }
            if (!(RenderPipelineManager.currentPipeline is HDRenderPipeline))
            {
                Debug.LogError("Activate HDRP before converting reflection assets.");
                return;
            }
            string directory = "Assets/ReflectionProbeOct";
            Directory.CreateDirectory(directory);
            string path = AssetDatabase.GenerateUniqueAssetPath(directory + "/" +
                string.Join("_", probe.name.Split(Path.GetInvalidFileNameChars())) + "-Oct.exr");
            var source = probe.texture;
            Undo.RecordObject(probe, "Convert reflection source to raw Oct");
            // Preserve the original Cube asset; only this probe's reference is replaced.
            HDBakedReflectionSystem.WriteOctahedralRadiance(source, path);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            HDBakedReflectionSystem.ConfigureOctahedralImporter(importer, probe);
            importer.maxTextureSize = Mathf.Max(64, source.width * 2);
            importer.SaveAndReimport();
            probe.useOctahedralRadiance = true;
            probe.SetTexture(probe.mode, AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            EditorUtility.SetDirty(probe);
            AssetDatabase.SaveAssets();
        }
    }
}
