using System;

[Serializable]
public class FaceDebugResponseData
{
    public string status;
    public string user_id;
    public string model_id;
    public int face_count;
    public FaceDebugData[] face_debug;
}