using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// メニューから、選択中の衣装のゲームオブジェクトにコンポーネントを付ける。
    /// </summary>
    internal static class AdapterMenu
    {
        const string ToolName = "Kurena-chan's A to T Adapter";

        [MenuItem("Tools/FluffLabs/" + ToolName)]
        static void AddFromToolsMenu()
        {
            AddTo(Selection.gameObjects);
        }

        [MenuItem("Tools/FluffLabs/" + ToolName, true)]
        static bool ValidateAddFromToolsMenu()
        {
            return Selection.gameObjects.Any(go => !EditorUtility.IsPersistent(go));
        }

        // ヒエラルキーの右クリックメニュー（複数選択時は、選択したオブジェクトごとに呼ばれる）
        [MenuItem("GameObject/FluffLabs/" + ToolName)]
        static void AddFromHierarchy(MenuCommand command)
        {
            if (command.context is GameObject target) AddTo(new[] { target });
            else AddTo(Selection.gameObjects);
        }

        [MenuItem("GameObject/FluffLabs/" + ToolName, true)]
        static bool ValidateAddFromHierarchy()
        {
            return ValidateAddFromToolsMenu();
        }

        /// <summary>
        /// コンポーネントを付けて（すでに付いていればそのまま）、そのゲームオブジェクトを選択する。
        /// </summary>
        static void AddTo(IEnumerable<GameObject> targets)
        {
            var added = new List<Object>();
            foreach (var go in targets)
            {
                if (go == null || EditorUtility.IsPersistent(go)) continue;
                if (go.GetComponent<KurenaA2TAdapter>() == null) Undo.AddComponent<KurenaA2TAdapter>(go);
                added.Add(go);
            }
            // 複数選択からの実行では選択を保つ（すでに選択されていなければ、付けたオブジェクトを選択する）
            if (added.Count > 0 && !added.All(o => Selection.objects.Contains(o))) Selection.objects = added.ToArray();
        }
    }
}
