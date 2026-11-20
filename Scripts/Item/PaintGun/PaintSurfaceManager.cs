using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 씬 내 모든 PaintableSurface를 등록/관리하며, 실제 브러시 연산을 수행하는 로컬 싱글톤.
/// 네트워크 오브젝트가 아님 - 각 클라이언트가 자기 로컬 인스턴스를 가지고 자기 화면만 그림.
/// </summary>
public class PaintSurfaceManager : MonoBehaviour
{
    public static PaintSurfaceManager Instance { get; private set; }

    [Header("Brush")]
    [SerializeField] private Shader brushShader; // Hidden/PaintBrush 셰이더 할당

    [Header("Paint Splatter (칠하기에만 적용, 지우기는 둥근 원)")]
    [Tooltip("자국 가장자리가 울퉁불퉁한 정도. 0이면 둥근 원")]
    [SerializeField, Range(0f, 1f)] private float edgeRoughness = 0.5f;
    [Tooltip("자국 주변에 튀는 방울의 양. 0이면 방울 없음")]
    [SerializeField, Range(0f, 1f)] private float dropletAmount = 0.5f;

    [Header("묶음 칠하기 (시작 페인트)")]
    [Tooltip("한 프레임에 실행하는 묶음 그리기 횟수. 묶음 하나는 한 표면에 자국 최대 16개. 크면 빨리 끝나지만 그 프레임이 무거워진다")]
    [SerializeField, Min(1)] private int maxBatchDrawsPerFrame = 8;

    private static readonly int BrushPosId = Shader.PropertyToID("_BrushPos");
    private static readonly int BrushRadiusId = Shader.PropertyToID("_BrushRadius");
    private static readonly int BrushValueId = Shader.PropertyToID("_BrushValue");
    private static readonly int SplatterId = Shader.PropertyToID("_Splatter");
    private static readonly int EdgeRoughnessId = Shader.PropertyToID("_EdgeRoughness");
    private static readonly int DropletAmountId = Shader.PropertyToID("_DropletAmount");
    private static readonly int DabsId = Shader.PropertyToID("_Dabs");
    private static readonly int DabNormalsId = Shader.PropertyToID("_DabNormals");
    private static readonly int DabCountId = Shader.PropertyToID("_DabCount");

    private const int BRUSH_PASS = 0;
    private const int AREA_PASS = 1;
    private const int BATCH_PASS = 2;

    // 셰이더의 MAX_DABS와 맞출 것. 셰이더 배열은 처음 넣은 크기로 고정되므로 항상 이 길이로 넣는다
    private const int MAX_DABS_PER_DRAW = 16;

    // 아직 등록되지 않은 표면의 자국은 기다렸다가 찍는다. 이 시간이 지나도 없으면 버린다 (초)
    private const float MISSING_SURFACE_TIMEOUT = 10f;

    // 픽셀당 면적 측정용 임시 텍스처 크기. 평균값만 필요하므로 표면 해상도보다 작아도 충분
    private const int AREA_MAP_SIZE = 512;

    // 칠해진 면적이 이보다 작으면(눈에 안 보이는 잔여 얼룩) 0으로 취급 (m²)
    private const float MIN_PAINTED_AREA = 0.01f;

    private Material _brushMaterial;
    private CommandBuffer _brushCommands;
    private readonly Dictionary<int, PaintableSurface> _surfaces = new();
    private readonly HashSet<int> _readbackInFlight = new(); // 표면별로 이미 읽기 요청이 진행 중인지 추적.
    private readonly HashSet<int> _readbackDirty = new();    // 읽는 도중 또 칠해진 표면. 읽기가 끝나면 한 번 더 읽는다
    private int _areaMeasuresInFlight;

    // 묶음 칠하기 대기열 (표면 ID → 아직 안 찍은 자국)
    private readonly Dictionary<int, List<PaintDab>> _pendingDabs = new();
    private readonly List<int> _pendingSurfaceIds = new();
    private int _pendingDabCount;
    private float _pendingSince;
    private readonly Vector4[] _dabPositions = new Vector4[MAX_DABS_PER_DRAW];
    private readonly Vector4[] _dabNormals = new Vector4[MAX_DABS_PER_DRAW];

    public IEnumerable<PaintableSurface> AllSurfaces => _surfaces.Values;

    /// <summary>EnqueueDabs로 받은 자국 중 찍었거나(또는 표면이 없어 버린) 수. 서버가 시작 페인트가 다 반영됐는지 볼 때 쓴다</summary>
    public int ProcessedDabCount { get; private set; }

    /// <summary>찍을 자국도, 진행 중인 마스크 읽기·면적 측정도 없음. 이때 표면들의 PaintedArea가 최신이다</summary>
    public bool IsIdle => _pendingDabCount == 0 && _readbackInFlight.Count == 0 && _areaMeasuresInFlight == 0;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (brushShader == null)
        {
            Debug.LogError("PaintSurfaceManager: brushShader가 할당되지 않았습니다.");
            return;
        }
        _brushMaterial = new Material(brushShader);
        _brushCommands = new CommandBuffer { name = "PaintBrush" };
    }

    private void OnDestroy()
    {
        _brushCommands?.Release();
        if (_brushMaterial != null)
        {
            Destroy(_brushMaterial);
        }
    }

    private void Update()
    {
        if (_pendingDabCount > 0) ProcessPendingDabs();
    }

    public void Register(int id, PaintableSurface surface)
    {
        if (_surfaces.ContainsKey(id))
        {
            Debug.LogWarning($"SurfaceId {id}가 중복 등록되었습니다: {surface.name}");
        }
        _surfaces[id] = surface;

        MeasureAreaPerPixel(surface);
    }

    /// <summary>
    /// 페인트 마스크 1픽셀이 덮는 평균 월드 면적(m²)을 GPU로 한 번 측정한다. <br/>
    /// 메시가 Read/Write 비활성이라 빌드에서는 스크립트로 정점을 읽을 수 없으므로, 메시를 UV 공간에 펼쳐 그리면서
    /// 픽셀마다 월드 면적을 기록하고 합산한다. 칠해진 픽셀 수 × 이 값 = 실제로 칠해진 면적
    /// </summary>
    private void MeasureAreaPerPixel(PaintableSurface surface)
    {
        if (_brushMaterial == null || surface.Canvas == null) return;

        var filter = surface.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return;
        Mesh mesh = filter.sharedMesh;

        RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)
            ? RenderTextureFormat.RFloat
            : RenderTextureFormat.RHalf;
        RenderTexture areaMap = RenderTexture.GetTemporary(AREA_MAP_SIZE, AREA_MAP_SIZE, 0, format);

        _brushCommands.Clear();
        _brushCommands.SetRenderTarget(areaMap);
        _brushCommands.ClearRenderTarget(false, true, Color.clear);
        _brushCommands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
        Matrix4x4 localToWorld = surface.transform.localToWorldMatrix;
        int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
        for (int i = 0; i < subMeshCount; i++)
        {
            _brushCommands.DrawMesh(mesh, localToWorld, _brushMaterial, i, AREA_PASS);
        }
        Graphics.ExecuteCommandBuffer(_brushCommands);

        int canvasPixels = surface.Canvas.width * surface.Canvas.height;
        _areaMeasuresInFlight++;
        AsyncGPUReadback.Request(areaMap, 0, TextureFormat.RFloat, request =>
        {
            _areaMeasuresInFlight--;
            RenderTexture.ReleaseTemporary(areaMap);

            if (request.hasError || surface == null)
            {
                Debug.LogWarning($"[PaintSurfaceManager] {(surface != null ? surface.name : "?")} 면적 측정 실패. 이 표면은 페인트 점수에 반영되지 않습니다.");
                return;
            }

            NativeArray<float> areas = request.GetData<float>();
            double totalArea = 0;
            int coveredPixels = 0;
            for (int i = 0; i < areas.Length; i++)
            {
                if (areas[i] > 0f)
                {
                    totalArea += areas[i];
                    coveredPixels++;
                }
            }

            if (coveredPixels == 0) return;

            // 측정용 텍스처에서 메시가 차지한 비율 = 실제 페인트 마스크에서 메시가 차지하는 비율
            float coveredRatio = coveredPixels / (float)areas.Length;
            surface.AreaPerPixel = (float)(totalArea / (coveredRatio * canvasPixels));

            // 측정보다 페인트 읽기가 먼저 끝났으면 그때는 면적을 0으로 셌으므로 지금 다시 계산한다 (시작 페인트)
            if (surface.PaintedPixels > 0) ApplyPaintedPixels(surface, surface.PaintedPixels);
        });
    }

    public void Unregister(int id)
    {
        _surfaces.Remove(id);
    }

    /// <summary>
    /// 지정된 표면에 월드 좌표 기준 브러시를 적용한다. (칠하기/지우기 공용, 로컬에서만 실행) <br/>
    /// 표면 메시를 UV 공간에 펼쳐 그리면서 픽셀마다 월드 거리를 재므로, 메시 크기/스케일/UV 배치와
    /// 상관없이 어느 표면이든 같은 실제 크기의 자국이 된다. 모서리에 찍으면 양쪽 면에 걸쳐 칠해진다.
    /// </summary>
    /// <param name="surfaceId">대상 표면 ID</param>
    /// <param name="worldPoint">브러시 중심 (레이가 맞은 월드 좌표)</param>
    /// <param name="radius">브러시 반경 (미터)</param>
    /// <param name="isPaint">true = 페인트 칠하기, false = 지우기</param>
    public void DrawAt(int surfaceId, Vector3 worldPoint, float radius, bool isPaint)
    {
        if (_brushMaterial == null) return;

        if (!_surfaces.TryGetValue(surfaceId, out var surface) || surface.Canvas == null)
        {
            Debug.LogWarning($"SurfaceId {surfaceId}를 찾을 수 없습니다.");
            return;
        }

        _brushMaterial.SetVector(BrushPosId, worldPoint);
        _brushMaterial.SetFloat(BrushRadiusId, Mathf.Max(0.001f, radius));
        _brushMaterial.SetFloat(BrushValueId, isPaint ? 1f : 0f);
        // 칠할 때만 페인트 자국 모양. 지우기(걸레)는 기존처럼 둥글게 깔끔히 지운다
        _brushMaterial.SetFloat(SplatterId, isPaint ? 1f : 0f);
        _brushMaterial.SetFloat(EdgeRoughnessId, edgeRoughness);
        _brushMaterial.SetFloat(DropletAmountId, dropletAmount);

        if (!DrawSurfaceMesh(surface, BRUSH_PASS)) return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            RequestContaminationReadback(surfaceId, surface);
        }
    }

    /// <summary>
    /// 자국 여러 개를 대기열에 넣는다. 매 프레임 maxBatchDrawsPerFrame만큼 나눠서, 같은 표면의 자국은 최대 16개씩 묶어 한 번에 찍는다.
    /// 시작 페인트처럼 한꺼번에 많이 찍을 때 쓴다 (로컬에서만 실행, 각 클라이언트가 같은 목록을 받아 각자 그림).
    /// </summary>
    public void EnqueueDabs(IReadOnlyList<PaintDab> dabs)
    {
        if (dabs == null || dabs.Count == 0) return;
        if (_pendingDabCount == 0) _pendingSince = Time.unscaledTime;

        foreach (PaintDab dab in dabs)
        {
            if (!_pendingDabs.TryGetValue(dab.SurfaceId, out List<PaintDab> list))
            {
                list = new List<PaintDab>();
                _pendingDabs[dab.SurfaceId] = list;
                _pendingSurfaceIds.Add(dab.SurfaceId);
            }
            list.Add(dab);
        }
        _pendingDabCount += dabs.Count;
    }

    private void ProcessPendingDabs()
    {
        if (_brushMaterial == null) return;

        bool isServer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        bool timedOut = Time.unscaledTime - _pendingSince > MISSING_SURFACE_TIMEOUT;
        int drawsLeft = maxBatchDrawsPerFrame;

        _brushMaterial.SetFloat(EdgeRoughnessId, edgeRoughness);
        _brushMaterial.SetFloat(DropletAmountId, dropletAmount);

        for (int i = _pendingSurfaceIds.Count - 1; i >= 0 && drawsLeft > 0; i--)
        {
            int surfaceId = _pendingSurfaceIds[i];
            List<PaintDab> list = _pendingDabs[surfaceId];

            if (!_surfaces.TryGetValue(surfaceId, out PaintableSurface surface) || surface.Canvas == null)
            {
                // 맵 표면이 아직 Start 전일 수 있으니 기다린다. 너무 오래 없으면 ID가 서버와 다른 것이므로 버린다
                if (!timedOut) continue;
                Debug.LogWarning($"[PaintSurfaceManager] SurfaceId {surfaceId}를 찾을 수 없어 자국 {list.Count}개를 버립니다. 맵의 Surface ID를 다시 정렬했는지 확인하세요.");
                RemovePendingSurface(i, surfaceId, list.Count);
                continue;
            }

            while (list.Count > 0 && drawsLeft > 0)
            {
                int take = Mathf.Min(MAX_DABS_PER_DRAW, list.Count);
                int start = list.Count - take;
                for (int d = 0; d < MAX_DABS_PER_DRAW; d++)
                {
                    if (d < take)
                    {
                        PaintDab dab = list[start + d];
                        _dabPositions[d] = new Vector4(dab.Position.x, dab.Position.y, dab.Position.z, Mathf.Max(0.001f, dab.Radius));
                        _dabNormals[d] = dab.Normal;
                    }
                    else
                    {
                        _dabPositions[d] = Vector4.zero;
                        _dabNormals[d] = Vector4.zero;
                    }
                }
                list.RemoveRange(start, take);
                _pendingDabCount -= take;
                ProcessedDabCount += take;
                drawsLeft--;

                _brushMaterial.SetVectorArray(DabsId, _dabPositions);
                _brushMaterial.SetVectorArray(DabNormalsId, _dabNormals);
                _brushMaterial.SetFloat(DabCountId, take);
                DrawSurfaceMesh(surface, BATCH_PASS);
            }

            if (isServer) RequestContaminationReadback(surfaceId, surface);
            if (list.Count == 0) RemovePendingSurface(i, surfaceId, 0);
        }
    }

    private void RemovePendingSurface(int index, int surfaceId, int droppedCount)
    {
        _pendingSurfaceIds.RemoveAt(index);
        _pendingDabs.Remove(surfaceId);
        _pendingDabCount -= droppedCount;
        ProcessedDabCount += droppedCount;
    }

    /// <summary>현재 브러시 머테리얼 설정으로 표면 메시를 UV 공간에 펼쳐 페인트 마스크에 그린다</summary>
    private bool DrawSurfaceMesh(PaintableSurface surface, int pass)
    {
        var filter = surface.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return false;
        Mesh mesh = filter.sharedMesh;

        _brushCommands.Clear();
        _brushCommands.SetRenderTarget(surface.Canvas);
        // 뷰/투영을 단위 행렬로 두면 셰이더의 UNITY_MATRIX_P에는 플랫폼 보정(렌더 텍스처 상하 반전 등)만 남는다
        _brushCommands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);

        // 서브메시(멀티 머테리얼)로 나뉜 메시도 전체 면이 빠짐없이 칠해지도록 전부 그린다
        Matrix4x4 localToWorld = surface.transform.localToWorldMatrix;
        int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
        for (int i = 0; i < subMeshCount; i++)
        {
            _brushCommands.DrawMesh(mesh, localToWorld, _brushMaterial, i, pass);
        }

        Graphics.ExecuteCommandBuffer(_brushCommands);
        return true;
    }

    /// <summary>
    /// 표면의 칠해진 픽셀을 비동기로 세어 오염 비율과 실제 칠해진 면적(m²)을 갱신한다.
    /// 같은 표면에 대해 이미 읽기 요청이 진행 중이면 새 요청을 보내지 않고 표시만 해 두었다가, 그 읽기가 끝나면 한 번 더 읽는다.
    /// (연사로 인해 초당 여러 번 호출되어도 읽기는 표면당 하나씩만 나가서 104만 픽셀 풀스캔이 겹쳐 쌓이지 않고,
    ///  읽는 도중 칠한 마지막 자국도 빠짐없이 점수에 들어간다)
    /// </summary>
    private void RequestContaminationReadback(int surfaceId, PaintableSurface surface)
    {
        if (_readbackInFlight.Contains(surfaceId))
        {
            _readbackDirty.Add(surfaceId);
            return;
        }
        _readbackInFlight.Add(surfaceId);

        AsyncGPUReadback.Request(surface.Canvas, 0, TextureFormat.RGBA32, request =>
        {
            _readbackInFlight.Remove(surfaceId);

            if (!request.hasError && surface != null)
            {
                NativeArray<Color32> rawData = request.GetData<Color32>();
                int paintedPixels = 0;
                for (int i = 0; i < rawData.Length; i++)
                {
                    if (rawData[i].r > 50) // 임계값
                    {
                        paintedPixels++;
                    }
                }
                ApplyPaintedPixels(surface, paintedPixels);
            }

            // 읽는 도중 또 칠해졌으면 지금 상태를 다시 읽는다
            if (_readbackDirty.Remove(surfaceId) && surface != null && surface.Canvas != null)
            {
                RequestContaminationReadback(surfaceId, surface);
            }
        });
    }

    /// <summary>칠해진 픽셀 수로 표면의 면적·비율을 갱신하고 전체 페인트 점수를 다시 계산한다 (서버 전용)</summary>
    private static void ApplyPaintedPixels(PaintableSurface surface, int paintedPixels)
    {
        surface.PaintedPixels = paintedPixels;

        // 페인트 점수는 비율이 아니라 실제 면적으로 매긴다 (큰 바닥이든 작은 기둥이든 같은 면적이면 같은 점수)
        float paintedArea = paintedPixels * surface.AreaPerPixel;

        // 잔여 얼룩(눈에 안 보이는 수준)은 0으로 취급. 비율 기준이면 큰 바닥에서 수 m²가 무시되므로 면적 기준
        if (paintedArea < MIN_PAINTED_AREA)
        {
            paintedArea = 0f;
            paintedPixels = 0;
        }

        surface.PaintedArea = paintedArea;

        // 참고용 비율 (소수점 2자리)
        int totalPixels = surface.Canvas != null ? surface.Canvas.width * surface.Canvas.height : 0;
        float percent = totalPixels > 0 ? (paintedPixels / (float)totalPixels) * 100f : 0f;
        surface.ContaminationPercent = Mathf.Round(percent * 100f) / 100f;

        ScoreManager.Instance?.RecalculateTotalContamination();
    }
}
