using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// くれなちゃん向けの計測値。A ポーズの衣装を T ポーズに合わせるときの上腕の回転（親空間）。
    /// </summary>
    public static class BuiltinRotations
    {
        public static readonly Quaternion LeftArm = new Quaternion(0.2505162f, 0.06436123f, -0.009133102f, 0.9659275f);
        public static readonly Quaternion RightArm = new Quaternion(0.250517458f, -0.06436173f, 0.009133262f, 0.9659271f);

        // 直接入力の初期値（上のクォータニオンをオイラー角にしたもの）
        public static readonly Vector3 LeftArmEuler = new Vector3(29.0214f, 7.8718f, 0.9569f);
        public static readonly Vector3 RightArmEuler = new Vector3(29.0216f, -7.8719f, -0.9569f);
    }

    /// <summary>
    /// 衣装のゲームオブジェクトに付けると、Scene ビューのプレビューとビルド時に、
    /// 上腕のメッシュを A ポーズから T ポーズに合わせる。メッシュのアセットは変更しない（NDMF による非破壊処理）。
    /// </summary>
    [AddComponentMenu("FluffLabs/Kurena-chan's A to T Adapter")]
    [DisallowMultipleComponent]
    public sealed class KurenaA2TAdapter : MonoBehaviour, INDMFEditorOnly
    {
        public enum RotationSource
        {
            Builtin,
            FromArmatures,
            Manual,
        }

        // 腕ボーンのオーバーライド（null なら自動検出）
        public Transform leftArmOverride;
        public Transform rightArmOverride;

        public RotationSource rotationSource = RotationSource.Builtin;
        public Transform beforeArmature;
        public Transform afterArmature;
        public Vector3 manualLeft = BuiltinRotations.LeftArmEuler;
        public Vector3 manualRight = BuiltinRotations.RightArmEuler;

        // 処理対象のメッシュのうち、ユーザーが初期値（腕にウェイトがあるかどうか）から切り替えたもの
        public List<SkinnedMeshRenderer> checkedMeshes = new List<SkinnedMeshRenderer>();
        public List<SkinnedMeshRenderer> uncheckedMeshes = new List<SkinnedMeshRenderer>();
    }
}
