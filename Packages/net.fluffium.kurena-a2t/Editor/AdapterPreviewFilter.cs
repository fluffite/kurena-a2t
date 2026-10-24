using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// NDMF のプレビューで、コンポーネントの設定どおりに T ポーズにしたメッシュを Scene ビューに表示する。
    /// 元のメッシュやシーンは変更しない。
    /// </summary>
    internal sealed class AdapterPreviewFilter : IRenderFilter
    {
        // NDMF のプレビューメニューとインスペクターから切り替えられる、このツールのプレビューのオン・オフ
        public static readonly TogglablePreviewNode PreviewNode = TogglablePreviewNode.Create(
            () => "Kurena-chan's A to T Adapter", "net.fluffium.kurena-a2t/Preview", true);

        public bool IsEnabled(ComputeContext context)
        {
            return context.Observe(PreviewNode.IsEnabled);
        }

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return PreviewNode;
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            var used = new HashSet<SkinnedMeshRenderer>();
            foreach (var adapter in context.GetComponentsByType<KurenaA2TAdapter>())
            {
                if (adapter == null) continue;
                ObserveSettings(context, adapter);

                var resolved = AdapterResolver.Resolve(adapter);
                if (resolved.Error != null) continue;
                foreach (var renderer in resolved.Renderers)
                {
                    // 同じメッシュに複数のコンポーネントがかかる場合は、最初のものだけを使う
                    if (!used.Add(renderer)) continue;
                    groups.Add(RenderGroup.For(renderer).WithData(adapter, (a, b) => a == b));
                }
            }
            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var adapter = group.GetData<KurenaA2TAdapter>();
            ObserveSettings(context, adapter);

            Mesh mesh = null;
            var resolved = AdapterResolver.Resolve(adapter);
            if (resolved.Error == null)
            {
                foreach (var (_, proxy) in proxyPairs)
                {
                    // 前段のプレビュー（MeshCutter など）の結果が入った proxy のメッシュを元に変換する
                    if (proxy is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        mesh = BindposeAdapter.CreateAdaptedMesh(smr, resolved.Arms);
                        mesh.hideFlags = HideFlags.DontSave;
                    }
                }
            }
            return Task.FromResult<IRenderFilterNode>(new Node(mesh));
        }

        /// <summary>
        /// 結果に影響するもの（コンポーネントの設定と、衣装内のメッシュ・ボーン構成）の変化を追いかける。
        /// </summary>
        static void ObserveSettings(ComputeContext context, KurenaA2TAdapter adapter)
        {
            context.Observe(adapter, a => JsonUtility.ToJson(a), (a, b) => a == b);
            context.GetComponentsInChildren<Transform>(adapter.gameObject, true);
            foreach (var renderer in context.GetComponentsInChildren<SkinnedMeshRenderer>(adapter.gameObject, true))
            {
                context.Observe(renderer, r => r.sharedMesh, (a, b) => a == b);
            }
        }

        sealed class Node : IRenderFilterNode
        {
            readonly Mesh _mesh;

            public Node(Mesh mesh)
            {
                _mesh = mesh;
            }

            public RenderAspects WhatChanged => RenderAspects.Mesh;

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context,
                RenderAspects updatedAspects)
            {
                // 前段でメッシュが変わったときは作り直す
                if (updatedAspects.HasFlag(RenderAspects.Mesh) || _mesh == null) return Task.FromResult<IRenderFilterNode>(null);
                return Task.FromResult<IRenderFilterNode>(this);
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (_mesh != null && proxy is SkinnedMeshRenderer smr) smr.sharedMesh = _mesh;
            }

            public void Dispose()
            {
                if (_mesh != null) Object.DestroyImmediate(_mesh);
            }
        }
    }
}
