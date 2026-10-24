using System.Collections.Generic;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// 衣装のルートから上腕ボーン（Left arm / Right arm）を自動検出する。
    /// </summary>
    internal static class ArmDetector
    {
        // 親に Humanoid アバターがない場合に使う、よくある上腕ボーン名（記号と大文字小文字を除いて比較する）
        static readonly string[] LeftCandidates = { "leftarm", "leftupperarm", "upperarml", "upperarmleft", "lupperarm", "arml", "larm" };
        static readonly string[] RightCandidates = { "rightarm", "rightupperarm", "upperarmr", "upperarmright", "rupperarm", "armr", "rarm" };

        /// <summary>
        /// 衣装のルートの親にある Humanoid アバターを返す（見つからなければ null）。
        /// </summary>
        public static Animator FindAvatar(Transform outfitRoot)
        {
            if (outfitRoot.parent == null) return null;
            var animator = outfitRoot.parent.GetComponentInParent<Animator>();
            return animator != null && animator.isHuman ? animator : null;
        }

        public static Transform Detect(Transform outfitRoot, bool left)
        {
            var avatar = FindAvatar(outfitRoot);
            if (avatar != null)
            {
                var avatarBone = avatar.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                if (avatarBone != null)
                {
                    var found = FindShallowest(outfitRoot, t => t.name == avatarBone.name);
                    if (found != null) return found;
                }
            }

            var candidates = new HashSet<string>(left ? LeftCandidates : RightCandidates);
            return FindShallowest(outfitRoot, t => candidates.Contains(Normalize(t.name)));
        }

        static Transform FindShallowest(Transform root, System.Predicate<Transform> match)
        {
            Transform best = null;
            var bestDepth = int.MaxValue;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || !match(t)) continue;
                var depth = 0;
                for (var p = t; p != root; p = p.parent) depth++;
                if (depth < bestDepth)
                {
                    best = t;
                    bestDepth = depth;
                }
            }
            return best;
        }

        static string Normalize(string name)
        {
            var chars = new List<char>();
            foreach (var c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) chars.Add(c);
            }
            return new string(chars.ToArray());
        }
    }
}
