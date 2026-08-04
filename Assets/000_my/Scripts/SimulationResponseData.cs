using System;

[Serializable]
public class CoordinatesData
{
    public float latitude;
    public float longitude;
}

[Serializable]
public class TrianglePreviewData
{
    public int face_index;
    public float temperature;
}

[Serializable]
public class RadiationSimulationData
{
    public int hour_local;
    public string datetime_local;
    public float solar_altitude_deg;
    public float solar_azimuth_deg;
    public float solar_zenith_deg;

    public float ghi_w_m2;
    public float dni_w_m2;
    public float dhi_w_m2;

    public float? linke_turbidity;
    public float albedo;
    public bool use_ground_reflected;

    public float beam_mean_w_m2;
    public float beam_max_w_m2;
    public float diffuse_mean_w_m2;
    public float diffuse_max_w_m2;
    public float reflected_mean_w_m2;
    public float reflected_max_w_m2;
    public float global_mean_w_m2;
    public float global_max_w_m2;

    public bool shadow_raycast_enabled;
}

[Serializable]
public class MetadataData
{
    public string user_id;
    public string model_id;
    public int year;
    public int day_of_year;
    public int[] times_of_day;
    public string timezone_str;

    public int faces_count;
    public int vertices_count;

    public string model_signature;
    public string file_sha256;

    public float? slope_mean_deg;
    public float? slope_min_deg;
    public float? slope_max_deg;
    public float? aspect_mean_deg;

    public int invalid_face_normals_count;

    public float temperature_min_c;
    public float temperature_mean_c;
    public float temperature_max_c;

    public float mean_radiation_reference_w_m2;
    public string mean_radiation_reference_method;

    public string clear_sky_model;
    public float? linke_turbidity;
    public float albedo;
    public bool use_ground_reflected;
    public float surface_temp_offset_c;

    public bool unity_triangle_mapping;
    public string temperature_json_format;
    public bool shadow_raycast_enabled;

    public CoordinatesData coordinates;
    public string created_at;

    public TrianglePreviewData[] triangle_temperature_preview;
    public RadiationSimulationData[] radiation_simulations;
}

[Serializable]
public class SimulationResponseData
{
    public string status;
    public string user_id;
    public string model_id;
    public MetadataData metadata;
    public FaceTemperatureData face_temperatures;
}