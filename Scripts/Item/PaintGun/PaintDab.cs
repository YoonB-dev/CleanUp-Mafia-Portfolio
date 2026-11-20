using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 표면 하나에 찍는 페인트 자국 하나. 시작 페인트처럼 서버가 한꺼번에 정해서 모든 클라이언트에 보내는 단위.
/// 메모리 그대로 직렬화되므로 필드를 바꾸면 서버와 클라이언트를 같이 빌드해야 한다.
/// </summary>
public struct PaintDab : INetworkSerializeByMemcpy
{
    public int SurfaceId;
    /// <summary>자국 중심 (월드)</summary>
    public Vector3 Position;
    /// <summary>자국 반경 (m)</summary>
    public float Radius;
    /// <summary>자국을 찍은 면의 노멀. 이 반대쪽을 보는 면(바닥 밑면, 얇은 벽 뒷면)에는 묻지 않는다</summary>
    public Vector3 Normal;

    public PaintDab(int surfaceId, Vector3 position, float radius, Vector3 normal)
    {
        SurfaceId = surfaceId;
        Position = position;
        Radius = radius;
        Normal = normal;
    }
}
