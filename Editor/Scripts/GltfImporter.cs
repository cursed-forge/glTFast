// SPDX-FileCopyrightText: 2023 Unity Technologies and the glTFast authors
// SPDX-License-Identifier: Apache-2.0

#if !GLTFAST_EDITOR_IMPORT_OFF

// glTFast is on the path to being official, so it should have highest priority as importer by default
// This define is included for completeness.
// Other glTF importers should specify this via AsmDef dependency, for example
// `com.unity.cloud.gltfast@3.0.0: HAVE_GLTFAST` and then checking here `#if HAVE_GLTFAST`
#if false
#define ANOTHER_IMPORTER_HAS_HIGHER_PRIORITY
#endif

#if !ANOTHER_IMPORTER_HAS_HIGHER_PRIORITY && !GLTFAST_FORCE_DEFAULT_IMPORTER_OFF
#define ENABLE_DEFAULT_GLB_IMPORTER
#endif
#if GLTFAST_FORCE_DEFAULT_IMPORTER_ON
#define ENABLE_DEFAULT_GLB_IMPORTER
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GLTFast.Logging;
using GLTFast.Utils;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GLTFast.Editor
{
    /// <summary>
    /// CursedForge fork: compression of the embedded glTF textures at Editor import, for Android only.
    /// glTFast creates every embedded PNG/JPEG as an uncompressed RGBA32 Texture2D sub-asset. On the
    /// Quest APK that made textures 2.6 GB of 2.8 GB. When the import context's selected build target is
    /// Android, <see cref="GltfImporter"/> compresses each created texture with
    /// <see cref="EditorUtility.CompressTexture(Texture2D, TextureFormat, TextureCompressionQuality)"/>.
    /// Other build targets (Standalone, the Editor, the simulator) keep the uncompressed textures.
    /// Define GLTFAST_ANDROID_TEXTURE_COMPRESSION_OFF in the Player scripting defines to switch it off
    /// (then bump the importer version or reimport the models, a define change alone does not reimport).
    /// </summary>
    public static class AndroidTextureCompression
    {
#if GLTFAST_ANDROID_TEXTURE_COMPRESSION_OFF
        /// <summary>False: the scripting define GLTFAST_ANDROID_TEXTURE_COMPRESSION_OFF is set.</summary>
        public const bool Enabled = false;
#else
        /// <summary>True: Android imports compress the embedded textures.</summary>
        public const bool Enabled = true;
#endif

        /// <summary>
        /// One format for every texture role (base colour, emission, occlusion, metallic-roughness, normal).
        /// ASTC keeps the RGBA channel layout, so alpha stays and the normal map stays XYZ in RGB, which
        /// glTFast's own normal decode (Runtime/Shader/Includes/Normal.cginc, NormalInTangentSpace_float)
        /// reads from .xy with x scaled by .w. No swizzle is needed.
        /// </summary>
        public const TextureFormat Format = TextureFormat.ASTC_6x6;

        /// <summary>Encoder quality for <see cref="EditorUtility.CompressTexture(Texture2D, TextureFormat, TextureCompressionQuality)"/>.</summary>
        public const TextureCompressionQuality Quality = TextureCompressionQuality.Normal;
    }


#if ENABLE_DEFAULT_GLB_IMPORTER
    // Version 2 (CursedForge fork): Android imports compress textures, so every model reimports once.
    [ScriptedImporter(2, new[] { "gltf", "glb" })]
#else
    [ScriptedImporter(2, null, overrideExts: new[] { "gltf","glb" })]
#endif
    class GltfImporter : ScriptedImporter
    {

        [SerializeField]
        EditorImportSettings editorImportSettings;

        [SerializeField]
        ImportSettings importSettings;

        [SerializeField]
        InstantiationSettings instantiationSettings;

        // These are used/read in the GltfImporterEditor
        // ReSharper disable NotAccessedField.Local
        [SerializeField]
        GltfAssetDependency[] assetDependencies;

        [SerializeField]
        internal LogItem[] reportItems;
        // ReSharper restore NotAccessedField.Local

        GltfImport m_Gltf;

        HashSet<string> m_ImportedNames;
        HashSet<Object> m_ImportedObjects;

        // static string[] GatherDependenciesFromSourceFile(string path) {
        //     // Called before actual import for each changed asset that is imported by this importer type
        //     // Extract the dependencies for the asset specified in path.
        //     // For asset dependencies that are discovered, return them in the string array, where the string is the path to asset
        //
        //     // TODO: Texture files with relative URIs should be included here
        //     return null;
        // }

        public override void OnImportAsset(AssetImportContext ctx)
        {

            reportItems = null;

            var downloadProvider = new EditorDownloadProvider();
            var logger = new CollectingLogger();

            m_Gltf = new GltfImport(
                downloadProvider,
                new UninterruptedDeferAgent(),
                null,
                logger
                );

            var gltfIcon = AssetDatabase.LoadAssetAtPath<Texture2D>($"Packages/{GltfGlobals.GltfPackageName}/Editor/UI/gltf-icon-bug.png");

            if (editorImportSettings == null)
            {
                // Design-time import specific settings
                editorImportSettings = new EditorImportSettings();
            }

            if (importSettings == null)
            {
                // Design-time import specific changes to default settings
                importSettings = new ImportSettings
                {
                    // Avoid naming conflicts by default
                    NodeNameMethod = NameImportMethod.OriginalUnique,
                    GenerateMipMaps = true,
                    AnimationMethod = AnimationMethod.Mecanim,
                };
            }

            if (instantiationSettings == null)
            {
                instantiationSettings = new InstantiationSettings();
            }

            // CursedForge fork: reading ctx.selectedBuildTarget registers a dependency on the build
            // target (Unity 6000.5 docs: "Returns the current build target and creates a dependency on
            // the target platform within a scripted importer"), so a platform switch reimports the model.
            var compressForAndroid = AndroidTextureCompression.Enabled
                && ctx.selectedBuildTarget == BuildTarget.Android;

            // CompressTexture works on the CPU copy of the pixels. Load readable for Android with a
            // copy of the settings (the serialized importSettings stay unchanged), then drop the CPU
            // copy after compression when the user settings ask for non-readable textures.
            var loadSettings = importSettings;
            if (compressForAndroid && !importSettings.TexturesReadable)
            {
                loadSettings = JsonUtility.FromJson<ImportSettings>(JsonUtility.ToJson(importSettings));
                loadSettings.TexturesReadable = true;
            }

            var success = AsyncHelpers.RunSync(() => m_Gltf.Load(ctx.assetPath, loadSettings));

            CollectingLogger instantiationLogger = null;
            if (success)
            {
                m_ImportedNames = new HashSet<string>();
                m_ImportedObjects = new HashSet<Object>();

                if (instantiationSettings.SceneObjectCreation == SceneObjectCreation.Never)
                {

                    // There *has* to be a common parent GameObject that gets
                    // added to the ScriptedImporter, so we overrule this
                    // setting.

                    instantiationSettings.SceneObjectCreation = SceneObjectCreation.WhenMultipleRootNodes;
                    Debug.LogWarning("SceneObjectCreation setting \"Never\" is not available for Editor (design-time) imports. Falling back to WhenMultipleRootNodes.", this);
                }

                instantiationLogger = new CollectingLogger();
                for (var sceneIndex = 0; sceneIndex < m_Gltf.SceneCount; sceneIndex++)
                {
                    try
                    {
                        ImportScene(ctx, sceneIndex, instantiationLogger);
                    }
                    catch (InvalidOperationException)
                    {
                        instantiationLogger.Error($"Failed creating scene {sceneIndex} instance.");
                    }

                    if (m_Gltf.MaterialsVariantsCount > 0)
                    {
                        for (var variantIndex = 0; variantIndex < m_Gltf.MaterialsVariantsCount; variantIndex++)
                        {
                            try
                            {
                                ImportScene(ctx, sceneIndex, instantiationLogger, variantIndex);
                            }
                            catch (InvalidOperationException)
                            {
                                instantiationLogger.Error($"Failed creating scene {sceneIndex} materials variant " +
                                    $"{variantIndex} instance.");
                            }
                        }
                    }
                }

                for (var i = 0; i < m_Gltf.TextureCount; i++)
                {
                    var texture = m_Gltf.GetTexture(i);
                    if (texture != null)
                    {
                        var textureAssetPath = AssetDatabase.GetAssetPath(texture);
                        if (string.IsNullOrEmpty(textureAssetPath))
                        {
                            if (compressForAndroid)
                            {
                                CompressForAndroid(texture, !importSettings.TexturesReadable);
                            }
                            AddObjectToAsset(ctx, $"textures/{texture.name}", texture);
                        }
                    }
                }

                for (var i = 0; i < m_Gltf.MaterialCount; i++)
                {
                    var mat = m_Gltf.GetMaterial(i);

                    // Overriding double-sided for GI baking
                    // Resolves problems with meshes that are not a closed
                    // volume at a potential minor cost of baking speed.
                    mat.doubleSidedGI = true;

                    if (mat != null)
                    {
                        AddObjectToAsset(ctx, $"materials/{mat.name}", mat);
                    }
                }

                if (m_Gltf.defaultMaterial != null)
                {
                    // If a default/fallback material was created, import it as well'
                    // to avoid (pink) objects without materials
                    var mat = m_Gltf.defaultMaterial;
                    AddObjectToAsset(ctx, $"materials/{mat.name}", mat);
                }

                var meshes = m_Gltf.Meshes;
                if (meshes != null)
                {
                    foreach (var mesh in meshes)
                    {
                        if (mesh == null)
                        {
                            continue;
                        }
                        if (editorImportSettings.generateSecondaryUVSet && !HasSecondaryUVs(mesh))
                        {
                            Unwrapping.GenerateSecondaryUVSet(mesh);
                        }
                        AddObjectToAsset(ctx, $"meshes/{mesh.name}", mesh);
                    }
                }

#if UNITY_ANIMATION
                var clips = m_Gltf.GetAnimationClips();
                if (clips != null) {
                    foreach (var animationClip in clips) {
                        if (animationClip == null) {
                            continue;
                        }
                        if (importSettings.AnimationMethod == AnimationMethod.Mecanim) {
                            var settings = AnimationUtility.GetAnimationClipSettings(animationClip);
                            settings.loopTime = true;
                            AnimationUtility.SetAnimationClipSettings (animationClip, settings);
                        }
                        AddObjectToAsset(ctx, $"animations/{animationClip.name}", animationClip);
                    }
                }

                // TODO seems the states don't properly connect to the Animator here
                // (would need to be saved as SubAssets of the AnimatorController)
                // var animators = go.GetComponentsInChildren<Animator>();
                // foreach (var animator in animators)
                // {
                //     var controller = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
                //     if (controller != null) {
                //         AddObjectToAsset(ctx, $"animatorControllers/{animator.name}", controller);
                //         foreach (var layer in controller.layers)
                //         {
                //             var stateMachine = layer.stateMachine;
                //             stateMachine.hideFlags = HideFlags.HideInHierarchy;
                //             if(stateMachine)
                //                 AddObjectToAsset(ctx, $"animatorControllers/{animator.name}/{stateMachine.name}", stateMachine);
                //         }
                //     }
                // }
#endif

                m_ImportedNames = null;
                m_ImportedObjects = null;
            }

            var deps = new List<GltfAssetDependency>();
            for (var index = 0; index < downloadProvider.assetDependencies.Count; index++)
            {
                var dependency = downloadProvider.assetDependencies[index];
                if (ctx.assetPath == dependency.originalUri)
                {
                    // Skip original gltf/glb file
                    continue;
                }

                var guid = AssetDatabase.AssetPathToGUID(dependency.originalUri);
                if (!string.IsNullOrEmpty(guid))
                {
                    dependency.assetPath = dependency.originalUri;
                    ctx.DependsOnSourceAsset(dependency.assetPath);
                }

                deps.Add(dependency);
            }

            assetDependencies = deps.ToArray();

            var reportItemList = new List<LogItem>();
            if (logger.Count > 0)
            {
                reportItemList.AddRange(logger.Items);
            }
            if (instantiationLogger?.Items != null)
            {
                reportItemList.AddRange(instantiationLogger.Items);
            }

            if (reportItemList.Any(x => x.Type == LogType.Error || x.Type == LogType.Exception))
            {
                Debug.LogError($"Failed to import {assetPath} (see inspector for details)", this);
            }
            reportItems = reportItemList.ToArray();
        }

        void ImportScene(
            AssetImportContext ctx,
            int sceneIndex,
            CollectingLogger instantiationLogger,
            int? materialsVariantIndex = null
            )
        {
            var scene = m_Gltf.GetSourceScene(sceneIndex);
            var sceneName = m_Gltf.GetSceneName(sceneIndex);
            string sceneObjectName = null;
            string variantNameSuffix = null;
            if (materialsVariantIndex.HasValue)
            {
                variantNameSuffix = m_Gltf.GetMaterialsVariantName(materialsVariantIndex.Value);
                if (string.IsNullOrEmpty(variantNameSuffix))
                {
                    variantNameSuffix = $"variant_{materialsVariantIndex.Value}";
                }
                sceneObjectName = $"{sceneName}_{variantNameSuffix}";
            }
            else
            {
                sceneObjectName = sceneName;
            }

            var go = new GameObject(sceneObjectName);
            var instantiator = new GameObjectInstantiator(m_Gltf, go.transform, instantiationLogger, instantiationSettings);
            var index = sceneIndex;
            var success = AsyncHelpers.RunSync(() => m_Gltf.InstantiateSceneAsync(instantiator, index));
            if (!success)
            {
                throw new InvalidOperationException("Instantiating scene failed");
            }
            var useFirstChild = true;
            var multipleNodes = scene.nodes.Length > 1;
            var hasAnimation = false;
#if UNITY_ANIMATION
            if (importSettings.AnimationMethod != AnimationMethod.None
                && (instantiationSettings.Mask & ComponentType.Animation) != 0) {
                var animationClips = m_Gltf.GetAnimationClips();
                if (animationClips != null && animationClips.Length > 0) {
                    hasAnimation = true;
                }
            }
#endif

            if (instantiationSettings.SceneObjectCreation == SceneObjectCreation.Never
                || instantiationSettings.SceneObjectCreation == SceneObjectCreation.WhenMultipleRootNodes && !multipleNodes)
            {
                // No scene GameObject was created, so the first
                // child is the first (and in this case only) node.

                // If there's animation, its clips' paths are relative
                // to the root GameObject (which will also carry the
                // `Animation` component. If not, we can import the the
                // first and only node as root directly.

                useFirstChild = !hasAnimation;
            }

            var sceneTransform = useFirstChild
                ? go.transform.GetChild(0)
                : go.transform;
            var sceneGo = sceneTransform.gameObject;

            if (materialsVariantIndex.HasValue)
            {
                sceneGo.name = $"{sceneGo.name}_{variantNameSuffix}";

                var variantsControl = instantiator.SceneInstance.MaterialsVariantsControl;
                AsyncHelpers.RunSync(() => variantsControl.ApplyMaterialsVariantAsync(materialsVariantIndex.Value));
            }
            AddObjectToAsset(ctx, $"scenes/{sceneObjectName}", sceneGo);
            if (sceneIndex == m_Gltf.DefaultSceneIndex && !materialsVariantIndex.HasValue)
            {
                ctx.SetMainObject(sceneGo);
            }
        }

        /// <summary>
        /// CursedForge fork: compresses one created texture in place (materials keep their reference).
        /// Keeps the mip chain (CompressTexture compresses every existing level) and the sRGB/linear state
        /// (checked after, a change logs a warning). Skips a texture that is already compressed (KTX).
        /// </summary>
        void CompressForAndroid(Texture2D texture, bool makeNonReadable)
        {
            var before = texture.graphicsFormat;
            if (GraphicsFormatUtility.IsCompressedFormat(before))
            {
                return;
            }
            if (!texture.isReadable)
            {
                Debug.LogWarning($"glTFast Android compression: texture {texture.name} in {assetPath} is not readable, it stays {before}.", this);
                return;
            }
            var wasSrgb = GraphicsFormatUtility.IsSRGBFormat(before);
            EditorUtility.CompressTexture(texture, AndroidTextureCompression.Format, AndroidTextureCompression.Quality);
            if (GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) != wasSrgb)
            {
                Debug.LogWarning($"glTFast Android compression: texture {texture.name} in {assetPath} changed sRGB state ({before} to {texture.graphicsFormat}).", this);
            }
            if (makeNonReadable)
            {
                // Drop the CPU copy so the device does not hold the texture twice.
                texture.Apply(false, true);
            }
        }

        void AddObjectToAsset(AssetImportContext ctx, string originalName, Object obj, Texture2D thumbnail = null)
        {
            if (m_ImportedObjects.Contains(obj))
            {
                return;
            }
            var uniqueAssetName = GetUniqueAssetName(originalName);
            ctx.AddObjectToAsset(uniqueAssetName, obj, thumbnail);
            m_ImportedNames.Add(uniqueAssetName);
            m_ImportedObjects.Add(obj);
        }

        string GetUniqueAssetName(string originalName)
        {
            if (string.IsNullOrWhiteSpace(originalName))
            {
                originalName = "Asset";
            }
            if (m_ImportedNames.Contains(originalName))
            {
                var i = 0;
                string extName;
                do
                {
                    extName = $"{originalName}_{i++}";
                } while (m_ImportedNames.Contains(extName));
                return extName;
            }
            return originalName;
        }

        static bool HasSecondaryUVs(Mesh mesh)
        {
            var attributes = mesh.GetVertexAttributes();
            return attributes.Any(attribute => attribute.attribute == VertexAttribute.TexCoord1);
        }
    }
}

#endif // !GLTFAST_EDITOR_IMPORT_OFF
