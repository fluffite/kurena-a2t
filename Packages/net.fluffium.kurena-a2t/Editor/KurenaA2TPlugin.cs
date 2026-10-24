using Fluffium.KurenaA2T;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(KurenaA2TPlugin))]

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// ビルド時に、コンポーネントの設定どおりに T ポーズにしたメッシュを衣装に反映し、コンポーネントを取り除く。
    /// </summary>
    internal sealed class KurenaA2TPlugin : Plugin<KurenaA2TPlugin>
    {
        public override string QualifiedName => "net.fluffium.kurena-a2t";
        public override string DisplayName => "Kurena-chan's A to T Adapter";

        protected override void Configure()
        {
            // 衣装のボーンを基準に計算するため、Modular Avatar が衣装をアバターに統合する前に処理する
            InPhase(BuildPhase.Transforming)
                .Run("Adapt A-pose outfit meshes to T-pose", Execute)
                .BeforePlugin("nadena.dev.modular-avatar")
                .PreviewingWith(new AdapterPreviewFilter());
        }

        static void Execute(BuildContext context)
        {
            foreach (var adapter in context.AvatarRootTransform.GetComponentsInChildren<KurenaA2TAdapter>(true))
            {
                var resolved = AdapterResolver.Resolve(adapter);
                if (resolved.Error != null)
                {
                    Debug.LogWarning($"[Kurena-chan's A to T Adapter] 「{adapter.name}」は処理しませんでした: {resolved.Error}", adapter);
                }
                else
                {
                    foreach (var renderer in resolved.Renderers)
                    {
                        var mesh = BindposeAdapter.CreateAdaptedMesh(renderer, resolved.Arms);
                        context.AssetSaver.SaveAsset(mesh);
                        renderer.sharedMesh = mesh;
                    }
                }
                Object.DestroyImmediate(adapter);
            }
        }
    }
}
