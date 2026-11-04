#if UNITY_6000_3_OR_NEWER
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace Coordinator.Editor
{
    internal static class CoordinatorMainToolbar
    {
        private const string ElementPath = "Coordinator/Window";

        [MainToolbarElement(
            ElementPath,
            defaultDockPosition = MainToolbarDockPosition.Left,
            defaultDockIndex = 10)]
        private static MainToolbarElement CreateOpenButton()
        {
            var icon = EditorGUIUtility.IconContent(EditorGUIUtility.isProSkin ? "d_FilterByLabel" : "FilterByLabel").image as Texture2D;
            var content = new MainToolbarContent(icon, "Open Coordinator");
            return new MainToolbarButton(content, CoordinatorWindow.Open);
        }
    }
}
#endif
