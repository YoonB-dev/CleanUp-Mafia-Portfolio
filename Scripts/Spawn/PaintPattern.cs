using System;
using UnityEngine;

/// <summary>
/// 시작 페인트 모양 하나. 페인트총과 같은 브러시 자국(dab) 여러 개를 평면 위에 늘어놓아 웅덩이·끌린 자국 같은 모양을 만든다.
/// 자국 위치는 찍을 면 기준 로컬 좌표(m)다: x = 오른쪽, y = 위(벽에 세워 찍을 때) / 무작위 방향(바닥).
/// 브러시가 월드 거리로 그려지므로 표면 UV·해상도와 상관없이 어디서나 같은 크기로 찍히고, 모서리에 걸치면 양쪽 면에 묻는다.
/// </summary>
[CreateAssetMenu(fileName = "PaintPattern", menuName = "Cleanup Mafia/Paint Pattern")]
public class PaintPattern : ScriptableObject
{
    public enum SurfaceKind
    {
        Floor, // 바닥에만
        Wall,  // 벽에만
        Any,   // 바닥·벽 반반
    }

    [Serializable]
    public struct Dab
    {
        [Tooltip("패턴 중심에서의 위치 (m). x = 오른쪽, y = 위")]
        public Vector2 offset;
        [Tooltip("자국 반경 (m). 페인트총 한 발이 0.2")]
        [Min(0.01f)] public float radius;
    }

    [Tooltip("찍을 수 있는 면")]
    [SerializeField] private SurfaceKind placement = SurfaceKind.Floor;
    [Tooltip("벽에 찍을 때 패턴의 +y를 실제 위쪽에 맞춘다 (흘러내린 자국처럼 방향이 있는 모양). 끄면 아무 각도로 돌린다. 바닥은 항상 아무 각도")]
    [SerializeField] private bool uprightOnWall;
    [Tooltip("찍을 때마다 이 범위에서 크기를 무작위로 곱한다")]
    [SerializeField] private Vector2 scaleRange = new(0.85f, 1.15f);
    [Tooltip("좌우 반전을 무작위로 섞는다")]
    [SerializeField] private bool allowMirror = true;
    [SerializeField] private Dab[] dabs = Array.Empty<Dab>();

    private float _estimatedArea = -1f;

    public SurfaceKind Placement => placement;
    public bool UprightOnWall => uprightOnWall;
    public bool AllowMirror => allowMirror;
    public Dab[] Dabs => dabs;

    /// <summary>크기 배율 1일 때 자국들이 덮는 넓이 어림값(m²). 겹친 곳은 한 번만 센다</summary>
    public float EstimatedArea
    {
        get
        {
            if (_estimatedArea < 0f) _estimatedArea = ComputeArea();
            return _estimatedArea;
        }
    }

    public float RandomScale() => UnityEngine.Random.Range(scaleRange.x, scaleRange.y);

    private void OnValidate()
    {
        scaleRange.x = Mathf.Max(0.05f, scaleRange.x);
        scaleRange.y = Mathf.Max(scaleRange.x, scaleRange.y);
        _estimatedArea = -1f;
    }

    /// <summary>
    /// 자국들을 감싸는 사각형을 격자로 훑어 어느 자국이든 안에 드는 칸의 넓이를 더한다.
    /// 방울과 울퉁불퉁한 가장자리는 평균적으로 상쇄되므로 반경 그대로의 원으로 본다.
    /// </summary>
    private float ComputeArea()
    {
        if (dabs == null || dabs.Length == 0) return 0f;

        const int GRID = 64;
        Vector2 min = new(float.MaxValue, float.MaxValue);
        Vector2 max = new(float.MinValue, float.MinValue);
        foreach (Dab dab in dabs)
        {
            min = Vector2.Min(min, dab.offset - Vector2.one * dab.radius);
            max = Vector2.Max(max, dab.offset + Vector2.one * dab.radius);
        }

        Vector2 cell = (max - min) / GRID;
        int covered = 0;
        for (int x = 0; x < GRID; x++)
        {
            for (int y = 0; y < GRID; y++)
            {
                Vector2 p = min + new Vector2((x + 0.5f) * cell.x, (y + 0.5f) * cell.y);
                foreach (Dab dab in dabs)
                {
                    if ((p - dab.offset).sqrMagnitude > dab.radius * dab.radius) continue;
                    covered++;
                    break;
                }
            }
        }
        return covered * cell.x * cell.y;
    }
}

/// <summary>패턴 하나와 나올 비율. 같은 목록 안에서 weight끼리 비교한다 (2면 1보다 두 배 자주)</summary>
[Serializable]
public struct PaintPatternWeight
{
    public PaintPattern pattern;
    [Min(0f)] public float weight;
}
