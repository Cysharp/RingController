using Android.Hardware;

namespace RingController;

public sealed class RingSensorModel
{
    public const string SensorStringType = "xiaomi.sensor.optical_tracking";

    readonly Sensor sensor;

    public Sensor Sensor => sensor;
    public string Name => sensor.Name ?? "";
    public string Vendor => sensor.Vendor ?? "";

    public RingSensorModel(Sensor sensor)
    {
        this.sensor = sensor;
    }

    public static RingSensorModel? GetSensor(SensorManager? sensorManager)
    {
        var ringSensor = sensorManager?.GetSensorList(SensorType.All)
            ?.FirstOrDefault(s => s.StringType == SensorStringType);
        return (ringSensor == null) ? null : new RingSensorModel(ringSensor);
    }

    public RingSensorData CreateRingSensorData(SensorEvent e)
    {
        var values = e.Values;
        if (values == null || values.Count < 8)
        {
            return default;
        }

        return new RingSensorData(
            sum_delta_x: (int)values[0],
            delta_x: (int)values[1],
            cal_sum_delta_x: (int)values[2],
            cal_sum_delta_x_angle: (int)values[3],
            post_angle: (int)values[4],
            nega_angle: (int)values[5],
            has_open_cam_event: values[6] != 0,
            motion_event: (int)values[7]
        );
    }
}

public readonly record struct RingSensorData
(
    int sum_delta_x,
    int delta_x,
    int cal_sum_delta_x,
    int cal_sum_delta_x_angle,
    int post_angle,
    int nega_angle,
    bool has_open_cam_event,
    int motion_event
)
{
    public int AbsoluteAngle => -(post_angle + nega_angle) % 360;
}