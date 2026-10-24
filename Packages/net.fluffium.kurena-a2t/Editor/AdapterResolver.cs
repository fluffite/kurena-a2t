using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// コンポーネントの設定から求めた、実際に処理する内容。
    /// </summary>
    internal sealed class ResolvedAdapter
    {
        public Transform LeftDetected;
        public Transform RightDetected;
        public Transform LeftArm;
        public Transform RightArm;

        // 回転量（腕が見つからない、または回転量を求められないときは null）
        public Quaternion? LeftDelta;
        public Quaternion? RightDelta;

        // 腕ボーンを参照しているメッシュと、そのうち腕にウェイトがあるか・処理対象として選ばれているか
        public readonly List<SkinnedMeshRenderer> Candidates = new List<SkinnedMeshRenderer>();
        public readonly List<bool> Weighted = new List<bool>();
        public readonly List<bool> Checked = new List<bool>();

        public readonly List<ArmRotation> Arms = new List<ArmRotation>();
        public readonly List<SkinnedMeshRenderer> Renderers = new List<SkinnedMeshRenderer>();

        // 処理できない理由（処理できるなら null）
        public string Error;
    }

    /// <summary>
    /// インスペクター・プレビュー・ビルドで共通に使う、設定の解決処理。
    /// </summary>
    internal static class AdapterResolver
    {
        // 腕のボーンにウェイトがあるか（メッシュのチェックの初期値）の計算結果
        static readonly Dictionary<string, bool> WeightCache = new Dictionary<string, bool>();

        public static ResolvedAdapter Resolve(KurenaA2TAdapter adapter)
        {
            var result = new ResolvedAdapter();
            result.Error = ResolveArms(adapter, result) ?? ResolveRotations(adapter, result) ?? ResolveRenderers(adapter, result);
            if (result.Error != null)
            {
                result.Arms.Clear();
                result.Renderers.Clear();
            }
            return result;
        }

        static string ResolveArms(KurenaA2TAdapter adapter, ResolvedAdapter result)
        {
            var outfitRoot = adapter.transform;
            var rootAnimator = outfitRoot.GetComponent<Animator>();
            if (rootAnimator != null && rootAnimator.isHuman) return "アバター本体ではなく、衣装のゲームオブジェクトに付けてください。";

            result.LeftDetected = ArmDetector.Detect(outfitRoot, true);
            result.RightDetected = ArmDetector.Detect(outfitRoot, false);
            result.LeftArm = adapter.leftArmOverride != null ? adapter.leftArmOverride : result.LeftDetected;
            result.RightArm = adapter.rightArmOverride != null ? adapter.rightArmOverride : result.RightDetected;

            if (adapter.leftArmOverride != null && EditorUtility.IsPersistent(adapter.leftArmOverride)
                || adapter.rightArmOverride != null && EditorUtility.IsPersistent(adapter.rightArmOverride))
            {
                return "オーバーライドには、シーン内のボーンを指定してください（プレハブアセットは指定できません）。";
            }
            if (result.LeftArm == null && result.RightArm == null)
            {
                return "腕ボーンを検出できませんでした。オーバーライドで指定してください。";
            }
            if (result.LeftArm != null && result.RightArm != null
                && (result.LeftArm == result.RightArm || result.LeftArm.IsChildOf(result.RightArm) || result.RightArm.IsChildOf(result.LeftArm)))
            {
                return "Left arm と Right arm には、互いに親子関係のない別々のボーンを指定してください。";
            }
            return null;
        }

        static string ResolveRotations(KurenaA2TAdapter adapter, ResolvedAdapter result)
        {
            var leftError = ResolveDelta(adapter, result.LeftArm, BuiltinRotations.LeftArm, adapter.manualLeft, out result.LeftDelta);
            var rightError = ResolveDelta(adapter, result.RightArm, BuiltinRotations.RightArm, adapter.manualRight, out result.RightDelta);
            var error = leftError ?? rightError;
            if (error != null) return error;

            if (result.LeftArm != null) result.Arms.Add(new ArmRotation(result.LeftArm, result.LeftDelta.Value));
            if (result.RightArm != null) result.Arms.Add(new ArmRotation(result.RightArm, result.RightDelta.Value));
            return null;
        }

        static string ResolveDelta(KurenaA2TAdapter adapter, Transform arm, Quaternion builtin, Vector3 manual, out Quaternion? delta)
        {
            delta = null;
            if (arm == null) return null;

            switch (adapter.rotationSource)
            {
                case KurenaA2TAdapter.RotationSource.Builtin:
                    delta = builtin;
                    return null;

                case KurenaA2TAdapter.RotationSource.Manual:
                    delta = Quaternion.Euler(manual);
                    return null;

                case KurenaA2TAdapter.RotationSource.FromArmatures:
                    if (adapter.beforeArmature == null || adapter.afterArmature == null) return "回転前・回転後のアーマチュアを指定してください。";
                    var before = FindDescendant(adapter.beforeArmature, arm.name);
                    var after = FindDescendant(adapter.afterArmature, arm.name);
                    if (before == null || after == null) return $"アーマチュアの中に「{arm.name}」が見つかりません。";
                    delta = after.localRotation * Quaternion.Inverse(before.localRotation);
                    return null;
            }
            return null;
        }

        static string ResolveRenderers(KurenaA2TAdapter adapter, ResolvedAdapter result)
        {
            var armBones = result.Arms.Select(a => a.Bone).ToList();
            result.Candidates.AddRange(BindposeAdapter.FindAffectedRenderers(adapter.transform, armBones));
            if (result.Candidates.Count == 0) return "腕ボーンを使っているメッシュが衣装の中に見つかりません。";

            foreach (var renderer in result.Candidates)
            {
                // 初期値は、腕のボーンにウェイトがあるメッシュだけオン
                var weighted = HasArmWeights(renderer, armBones);
                var isChecked = adapter.checkedMeshes.Contains(renderer) || weighted && !adapter.uncheckedMeshes.Contains(renderer);
                result.Weighted.Add(weighted);
                result.Checked.Add(isChecked);
                if (isChecked) result.Renderers.Add(renderer);
            }
            return result.Renderers.Count == 0 ? "処理対象のメッシュを 1 つ以上選んでください。" : null;
        }

        static bool HasArmWeights(SkinnedMeshRenderer renderer, List<Transform> armBones)
        {
            var key = renderer.GetInstanceID() + ":" + renderer.sharedMesh.GetInstanceID();
            foreach (var bone in armBones) key += ":" + bone.GetInstanceID();
            if (!WeightCache.TryGetValue(key, out var weighted))
            {
                weighted = BindposeAdapter.HasWeightsOnBones(renderer, armBones);
                WeightCache[key] = weighted;
            }
            return weighted;
        }

        static Transform FindDescendant(Transform root, string name)
        {
            return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
        }
    }
}
