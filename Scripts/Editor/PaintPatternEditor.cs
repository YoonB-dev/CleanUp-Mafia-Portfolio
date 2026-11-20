#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// PaintPattern 인스펙터 아래에 자국 배치를 위에서 내려다본 그림으로 보여 준다 (실제 자국의 울퉁불퉁한 가장자리·방울은 빠진 단순 원).
/// 격자 한 칸 = 0.5m, 위쪽 = 패턴 +y(벽에 세워 찍을 때 위).
/// </summary>
[CustomEditor(typeof(PaintPattern))]
public class PaintPatternEditor : Editor
{
    private const float PREVIEW_SIZE = 260f;
    private const float GRID_STEP = 0.5f;
    private static readonly Color PaintColor = new(0.55f, 0.06f, 0.05f, 0.85f);

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var pattern = (PaintPattern)target;
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField($"어림 면적: {pattern.EstimatedArea:0.00} m² (크기 배율 1 기준)", EditorStyles.boldLabel);

        Rect rect = GUILayoutUtility.GetRect(PREVIEW_SIZE, PREVIEW_SIZE, GUILayout.ExpandWidth(false));
        if (Event.current.type == EventType.Repaint) DrawPreview(rect, pattern);
    }

    private static void DrawPreview(Rect rect, PaintPattern pattern)
    {
        EditorGUI.DrawRect(rect, new Color(0.82f, 0.8f, 0.76f));

        PaintPattern.Dab[] dabs = pattern.Dabs;
        float extent = 1f;
        if (dabs != null)
        {
            foreach (PaintPattern.Dab dab in dabs)
            {
                extent = Mathf.Max(extent, Mathf.Abs(dab.offset.x) + dab.radius, Mathf.Abs(dab.offset.y) + dab.radius);
            }
        }
        float pixelsPerMeter = rect.width * 0.5f / (extent * 1.1f);
        Vector2 center = rect.center;

        Handles.BeginGUI();

        Handles.color = new Color(0f, 0f, 0f, 0.12f);
        for (float m = 0f; m <= extent * 1.1f; m += GRID_STEP)
        {
            float o = m * pixelsPerMeter;
            Handles.DrawLine(new Vector3(center.x + o, rect.yMin), new Vector3(center.x + o, rect.yMax));
            Handles.DrawLine(new Vector3(center.x - o, rect.yMin), new Vector3(center.x - o, rect.yMax));
            Handles.DrawLine(new Vector3(rect.xMin, center.y + o), new Vector3(rect.xMax, center.y + o));
            Handles.DrawLine(new Vector3(rect.xMin, center.y - o), new Vector3(rect.xMax, center.y - o));
        }

        if (dabs != null)
        {
            Handles.color = PaintColor;
            foreach (PaintPattern.Dab dab in dabs)
            {
                // GUI 좌표는 y가 아래로 커지므로 뒤집는다
                Vector3 p = new(center.x + dab.offset.x * pixelsPerMeter, center.y - dab.offset.y * pixelsPerMeter);
                Handles.DrawSolidDisc(p, Vector3.forward, dab.radius * pixelsPerMeter);
            }
        }

        // 패턴 중심(찍는 기준점)
        Handles.color = Color.black;
        Handles.DrawWireDisc(center, Vector3.forward, 3f);

        Handles.EndGUI();
    }
}
#endif
