using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// シーンを変更せずに変換後の見た目を Scene ビューに表示する。
    /// 元の SkinnedMeshRenderer は Scene ビューの表示だけを隠し（SceneVisibilityManager）、
    /// 保存されない一時オブジェクトに変換後のメッシュを表示する。
    /// </summary>
    internal sealed class AdapterPreview
    {
        readonly List<GameObject> _proxies = new List<GameObject>();
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly List<GameObject> _hiddenOriginals = new List<GameObject>();

        public bool IsActive => _proxies.Count > 0;

        public void Show(IList<SkinnedMeshRenderer> renderers, IList<ArmRotation> arms)
        {
            Hide();

            var visibility = SceneVisibilityManager.instance;
            foreach (var renderer in renderers)
            {
                var mesh = BindposeAdapter.CreateAdaptedMesh(renderer, arms);
                mesh.hideFlags = HideFlags.HideAndDontSave;
                _meshes.Add(mesh);

                var proxy = new GameObject(renderer.name + " (A2T Preview)");
                proxy.hideFlags = HideFlags.HideAndDontSave;
                proxy.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
                proxy.transform.localScale = renderer.transform.lossyScale;

                var proxyRenderer = proxy.AddComponent<SkinnedMeshRenderer>();
                proxyRenderer.sharedMesh = mesh;
                proxyRenderer.bones = renderer.bones;
                proxyRenderer.rootBone = renderer.rootBone;
                proxyRenderer.sharedMaterials = renderer.sharedMaterials;
                proxyRenderer.localBounds = renderer.localBounds;
                proxyRenderer.updateWhenOffscreen = true;
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    proxyRenderer.SetBlendShapeWeight(i, renderer.GetBlendShapeWeight(i));
                }
                _proxies.Add(proxy);

                var original = renderer.gameObject;
                if (!visibility.IsHidden(original))
                {
                    visibility.Hide(original, false);
                    _hiddenOriginals.Add(original);
                }
            }
            SceneView.RepaintAll();
        }

        public void Hide()
        {
            foreach (var proxy in _proxies)
            {
                if (proxy != null) Object.DestroyImmediate(proxy);
            }
            foreach (var mesh in _meshes)
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }

            var visibility = SceneVisibilityManager.instance;
            foreach (var original in _hiddenOriginals)
            {
                if (original != null) visibility.Show(original, false);
            }

            _proxies.Clear();
            _meshes.Clear();
            _hiddenOriginals.Clear();
            SceneView.RepaintAll();
        }
    }
}
