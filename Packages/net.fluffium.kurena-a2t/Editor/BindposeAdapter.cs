using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// 腕ボーンと、その親空間で加える回転量の組。
    /// </summary>
    internal sealed class ArmRotation
    {
        public readonly Transform Bone;
        public readonly Quaternion Delta;

        public ArmRotation(Transform bone, Quaternion delta)
        {
            Bone = bone;
            Delta = delta;
        }

        /// <summary>
        /// 腕ボーンの localRotation に親空間で Delta を掛けたときに、
        /// 腕以下のボーンが受けるワールド空間の剛体変換を返す。
        /// </summary>
        public Matrix4x4 ComputeWorldTransform()
        {
            var parent = Bone.parent != null ? Bone.parent.localToWorldMatrix : Matrix4x4.identity;
            var local = Matrix4x4.Translate(Bone.localPosition)
                        * Matrix4x4.Rotate(Delta)
                        * Matrix4x4.Translate(-Bone.localPosition);
            return parent * local * parent.inverse;
        }
    }

    /// <summary>
    /// ボーンを動かさずに、bindpose の書き換えだけで腕の回転と同じ見た目の変化をメッシュに与える。
    /// </summary>
    internal static class BindposeAdapter
    {
        /// <summary>
        /// バックアップの複製に使う Unsupported の API の動作を確認した Unity のバージョン。
        /// </summary>
        public const string VerifiedUnityVersion = "2022.3";

        public static bool IsVerifiedUnityVersion => Application.unityVersion.StartsWith(VerifiedUnityVersion + ".");

        /// <summary>
        /// 衣装のルート以下で、指定した腕ボーン以下のボーンを参照している SkinnedMeshRenderer を集める。
        /// </summary>
        public static List<SkinnedMeshRenderer> FindAffectedRenderers(Transform outfitRoot, IList<Transform> armBones)
        {
            var result = new List<SkinnedMeshRenderer>();
            if (armBones.Count == 0) return result;

            foreach (var renderer in outfitRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                foreach (var bone in renderer.bones)
                {
                    if (bone != null && IsUnderAny(bone, armBones))
                    {
                        result.Add(renderer);
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 指定した腕ボーン以下のボーンに、0 より大きいウェイトを持つ頂点があるか。
        /// </summary>
        public static bool HasWeightsOnBones(SkinnedMeshRenderer renderer, IList<Transform> armBones)
        {
            var bones = renderer.bones;
            var targets = new bool[bones.Length];
            var any = false;
            for (var i = 0; i < bones.Length; i++)
            {
                targets[i] = bones[i] != null && IsUnderAny(bones[i], armBones);
                any |= targets[i];
            }
            if (!any) return false;

            foreach (var weight in renderer.sharedMesh.GetAllBoneWeights())
            {
                if (weight.weight > 0f && weight.boneIndex < targets.Length && targets[weight.boneIndex]) return true;
            }
            return false;
        }

        static bool IsUnderAny(Transform bone, IList<Transform> armBones)
        {
            foreach (var arm in armBones)
            {
                if (bone.IsChildOf(arm)) return true;
            }
            return false;
        }

        /// <summary>
        /// bindpose を書き換えたメッシュの複製を作る（アセットとしては保存しない）。
        /// 腕以下のボーン b について B'_b = W_b^-1 * M * W_b * B_b とする（M は腕の回転によるワールド空間の剛体変換）。
        /// </summary>
        public static Mesh CreateAdaptedMesh(SkinnedMeshRenderer renderer, IList<ArmRotation> arms)
        {
            var transforms = new Matrix4x4[arms.Count];
            for (var i = 0; i < arms.Count; i++) transforms[i] = arms[i].ComputeWorldTransform();

            var mesh = Object.Instantiate(renderer.sharedMesh);
            mesh.name = renderer.sharedMesh.name + "_A2T";

            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var count = Mathf.Min(bones.Length, bindposes.Length);
            for (var i = 0; i < count; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;
                for (var k = 0; k < arms.Count; k++)
                {
                    if (!bone.IsChildOf(arms[k].Bone)) continue;
                    var world = bone.localToWorldMatrix;
                    bindposes[i] = world.inverse * transforms[k] * world * bindposes[i];
                    break;
                }
            }
            mesh.bindposes = bindposes;
            return mesh;
        }

        /// <summary>
        /// bindpose を書き換えたメッシュをすべて 1 つの .asset ファイルにまとめて保存する。
        /// 既存のファイルは置き換える（保存ダイアログで上書きの確認が済んでいる前提）。
        /// </summary>
        public static List<Mesh> SaveMeshes(IList<SkinnedMeshRenderer> renderers, IList<ArmRotation> arms, string path)
        {
            var meshes = new List<Mesh>();
            foreach (var renderer in renderers) meshes.Add(CreateAdaptedMesh(renderer, arms));

            if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(meshes[0], path);
            for (var i = 1; i < meshes.Count; i++) AssetDatabase.AddObjectToAsset(meshes[i], meshes[0]);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            return meshes;
        }

        /// <summary>
        /// bindpose を書き換えたメッシュを、メッシュごとに 1 つの .asset ファイルとして folder に保存する。
        /// 同名のファイルが既にあれば、GUID を保ったまま中身を上書きする（以前の割り当てが外れないように）。
        /// </summary>
        public static List<Mesh> SaveMeshesSeparately(IList<SkinnedMeshRenderer> renderers, IList<ArmRotation> arms, string folder)
        {
            EnsureFolder(folder);

            var meshes = new List<Mesh>();
            var usedPaths = new HashSet<string>();
            foreach (var renderer in renderers)
            {
                var mesh = CreateAdaptedMesh(renderer, arms);
                var path = folder + "/" + mesh.name + ".asset";
                // 同じ名前のメッシュが複数あるときは、今回の保存同士で上書きしないよう別名にする
                if (!usedPaths.Add(path))
                {
                    path = AssetDatabase.GenerateUniqueAssetPath(path);
                    usedPaths.Add(path);
                }

                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null)
                {
                    EditorUtility.CopySerialized(mesh, existing);
                    Object.DestroyImmediate(mesh);
                    mesh = existing;
                    EditorUtility.SetDirty(mesh);
                }
                else
                {
                    AssetDatabase.CreateAsset(mesh, path);
                }
                meshes.Add(mesh);
            }
            AssetDatabase.SaveAssets();
            return meshes;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var slash = folder.LastIndexOf('/');
            var parent = folder.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folder.Substring(slash + 1));
        }

        /// <summary>
        /// 各 SkinnedMeshRenderer のメッシュを置き換える（Undo 可能。Undo のグループは呼び出し側でまとめる）。
        /// backup が true なら、置き換え前に衣装のルートを複製し、"_Backup" を付けて非アクティブにしておく。
        /// </summary>
        public static GameObject ReplaceMeshes(Transform outfitRoot, IList<SkinnedMeshRenderer> renderers, IList<Mesh> meshes, bool backup)
        {
            GameObject backupObject = null;
            if (backup)
            {
                backupObject = DuplicateForBackup(outfitRoot);
                Undo.RecordObject(backupObject, "Kurena-chan's A to T Adapter");
                backupObject.name = outfitRoot.name + "_Backup";
                backupObject.SetActive(false);
            }

            for (var i = 0; i < renderers.Count; i++)
            {
                Undo.RecordObject(renderers[i], "Kurena-chan's A to T Adapter");
                renderers[i].sharedMesh = meshes[i];
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderers[i]);
            }
            return backupObject;
        }

        /// <summary>
        /// 衣装のルートを、ヒエラルキーの Duplicate（Ctrl+D）と同じ方法で複製する（Prefab とのつながりを保つ）。
        /// Unsupported の API を使うため、複製できなかったときは通常の複製（Prefab とのつながりなし）にする。
        /// </summary>
        static GameObject DuplicateForBackup(Transform outfitRoot)
        {
            if (!IsVerifiedUnityVersion)
            {
                Debug.LogWarning($"[Kurena-chan's A to T Adapter] バックアップの複製は Unity {VerifiedUnityVersion} で動作を確認した方法で行っています。" +
                                 $"現在の Unity（{Application.unityVersion}）では、Prefab とのつながりを保てない場合があります。");
            }

            // Duplicate は選択中のオブジェクトを複製するので、一時的に衣装のルートだけを選択する
            var previousSelection = Selection.objects;
            var previousActive = Selection.activeObject;
            GameObject duplicate = null;
            try
            {
                Selection.objects = new Object[] { outfitRoot.gameObject };
                Unsupported.DuplicateGameObjectsUsingPasteboard();
                var selected = Selection.activeGameObject;
                if (selected != null && selected != outfitRoot.gameObject && selected.transform.parent == outfitRoot.parent)
                {
                    duplicate = selected;
                }
            }
            finally
            {
                Selection.objects = previousSelection;
                Selection.activeObject = previousActive;
            }

            if (duplicate != null) return duplicate;

            Debug.LogWarning("[Kurena-chan's A to T Adapter] Prefab とのつながりを保った複製ができなかったため、通常の複製でバックアップを作成しました。");
            duplicate = Object.Instantiate(outfitRoot.gameObject, outfitRoot.parent);
            duplicate.transform.SetSiblingIndex(outfitRoot.GetSiblingIndex() + 1);
            Undo.RegisterCreatedObjectUndo(duplicate, "Kurena-chan's A to T Adapter");
            return duplicate;
        }
    }
}
