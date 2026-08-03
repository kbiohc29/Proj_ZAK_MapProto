using UnityEngine;

/// <summary>
/// WGS84 경위도 → 로컬 미터 좌표 변환.
/// 도시~전국 스케일 프로토타입에는 등장방형(equirectangular) 근사로 충분하다.
/// anchor(기준점)를 맵 중심에 두어 float 정밀도 문제를 피한다.
/// </summary>
public static class GeoUtil
{
    public const double EarthRadius = 6378137.0;

    // 기준점 (부산 시청 근처). 다른 도시 테스트 시 바꿔주면 됨.
    public static double AnchorLon = 129.0756;
    public static double AnchorLat = 35.1796;

    public static Vector3 LonLatToLocal(double lon, double lat)
    {
        double x = (lon - AnchorLon) * Mathf.Deg2Rad * EarthRadius
                   * System.Math.Cos(AnchorLat * Mathf.Deg2Rad);
        double z = (lat - AnchorLat) * Mathf.Deg2Rad * EarthRadius;
        return new Vector3((float)x, 0f, (float)z);
    }
}
