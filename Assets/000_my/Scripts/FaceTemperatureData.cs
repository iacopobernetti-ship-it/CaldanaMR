using System;

[Serializable]
public class FaceTemperatureData
{
    public string temperature_unit;
    public int face_count;
    public float[] temperatures;
}