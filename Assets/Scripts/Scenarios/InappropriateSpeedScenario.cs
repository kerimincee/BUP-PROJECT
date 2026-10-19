using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityStandardAssets.Utility;
using VehicleBehaviour;

/// <summary>
/// "Koşullara Uygun Olmayan Hız" sürücü değerlendirme senaryosu.
/// Aracı 67 km/s ile başlatır, kontrollü bir yaya geçişi oluşturur ve sürücünün
/// tehlikeye yaklaşırken 50 km/s sınırına zamanında uyup uymadığını kaydeder.
/// </summary>
public class InappropriateSpeedScenario : MonoBehaviour, IExperimentModifier
{
    [Header("Scenario configuration")]
    [SerializeField] float speedLimitKmh = 50f;
    [SerializeField] float initialSpeedKmh = 67f;
    [SerializeField] float crossingDistance = 48f;
    [SerializeField] float pedestrianTriggerDistance = 32f;
    [SerializeField] float safeDecisionDistance = 12f;
    [SerializeField] float pedestrianLateralOffset = 4.5f;
    [SerializeField] float pedestrianCrossingSpeed = 1.4f;
    [SerializeField] float trialTimeoutSeconds = 35f;

    const float PedestrianCollisionDistance = 1.25f;
    const float PedestrianNearMissDistance = 2.25f;

    WheelVehicle vehicle;
    Rigidbody vehicleBody;
    AIPedestrian pedestrian;
    Animator pedestrianAnimator;
    Vector3 approachForward;
    Vector3 approachRight;
    Vector3 crossingCenter;
    Vector3 pedestrianStart;
    Vector3 pedestrianEnd;

    bool initialized;
    bool pedestrianReleased;
    bool decisionCaptured;
    bool finished;
    bool collided;
    bool nearMiss;
    float scenarioStartTime;
    float hazardStartTime;
    float reactionTime = -1f;
    float speedAtHazard;
    float speedAtDecisionLine = -1f;
    float minimumSpeed = float.MaxValue;
    float closestPedestrianDistance = float.MaxValue;
    float previousSpeed;
    float maximumDeceleration;
    string phase = "Hazırlanıyor";
    string result = "Değerlendirme sürüyor";
    string resultFile = "";
    readonly List<Sample> samples = new List<Sample>(2048);

    GUIStyle panelStyle;
    GUIStyle titleStyle;
    GUIStyle valueStyle;
    GUIStyle warningStyle;

    struct Sample
    {
        public float time;
        public float speed;
        public float distanceToCrossing;
        public float distanceToPedestrian;
        public bool pedestrianMoving;
    }

    void Start()
    {
        StartCoroutine(InitializeScenario());
    }

    IEnumerator InitializeScenario()
    {
        phase = "Sürücü aracı bekleniyor";
        float deadline = Time.realtimeSinceStartup + 15f;

        while (Time.realtimeSinceStartup < deadline)
        {
            var manager = NetworkingManager.Instance;
            var avatar = manager != null && manager.PlayerSystem != null
                ? manager.PlayerSystem.LocalPlayer
                : null;

            if (avatar != null)
            {
                vehicle = avatar.GetComponent<WheelVehicle>();
                if (vehicle == null)
                    vehicle = avatar.GetComponentInChildren<WheelVehicle>();
                if (vehicle != null)
                    break;
            }
            yield return null;
        }

        if (vehicle == null)
        {
            phase = "Başlatılamadı";
            result = "Sürücü aracı bulunamadı. Bu senaryoyu Driver rolüyle başlatın.";
            Debug.LogError("[Koşullara Uygun Olmayan Hız] Sürücü aracı bulunamadı.");
            yield break;
        }

        vehicleBody = vehicle.GetComponent<Rigidbody>();
        if (vehicleBody == null)
        {
            phase = "Başlatılamadı";
            result = "Araç Rigidbody bileşeni bulunamadı.";
            Debug.LogError("[Koşullara Uygun Olmayan Hız] Araç Rigidbody bileşeni bulunamadı.");
            yield break;
        }

        pedestrian = FindObjectOfType<AIPedestrian>();
        if (pedestrian == null)
        {
            phase = "Başlatılamadı";
            result = "Senaryo yayası bulunamadı.";
            Debug.LogError("[Koşullara Uygun Olmayan Hız] AIPedestrian bulunamadı.");
            yield break;
        }

        // Ağ sistemi yayayı kendi yoluna bağladıktan sonra bu senaryo kontrolü devralır.
        yield return new WaitUntil(() => Time.timeScale > 0f);
        yield return new WaitForFixedUpdate();

        pedestrian.enabled = false;
        var tracker = pedestrian.GetComponent<WaypointProgressTracker>();
        if (tracker != null)
            tracker.enabled = false;
        pedestrianAnimator = pedestrian.GetComponent<Animator>();

        approachForward = Vector3.ProjectOnPlane(vehicle.transform.forward, Vector3.up).normalized;
        approachRight = Vector3.Cross(Vector3.up, approachForward).normalized;
        crossingCenter = vehicle.transform.position + approachForward * crossingDistance;
        crossingCenter = PutOnGround(crossingCenter, vehicle.transform.position.y);
        pedestrianStart = PutOnGround(crossingCenter + approachRight * pedestrianLateralOffset, crossingCenter.y);
        pedestrianEnd = PutOnGround(crossingCenter - approachRight * pedestrianLateralOffset, crossingCenter.y);

        pedestrian.transform.position = pedestrianStart;
        pedestrian.transform.rotation = Quaternion.LookRotation((pedestrianEnd - pedestrianStart).normalized, Vector3.up);
        SetPedestrianAnimation(0f);

        CreateCrosswalk();
        CreateSpeedLimitSign();

        vehicleBody.velocity = approachForward * (initialSpeedKmh / 3.6f);
        vehicleBody.angularVelocity = Vector3.zero;
        previousSpeed = initialSpeedKmh;
        scenarioStartTime = Time.time;
        phase = "50 km/s hız sınırı — yaya geçidine yaklaşım";
        initialized = true;

        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[Koşullara Uygun Olmayan Hız] Senaryo başladı. İlk hız: {0:0.0} km/s, limit: {1:0} km/s.",
            initialSpeedKmh, speedLimitKmh));
    }

    void Update()
    {
        if (!initialized || finished)
            return;

        float elapsed = Time.time - scenarioStartTime;
        float speed = Mathf.Abs(vehicle.Speed);
        float distanceToCrossing = Vector3.Dot(crossingCenter - vehicle.transform.position, approachForward);

        if (!pedestrianReleased && distanceToCrossing <= pedestrianTriggerDistance)
        {
            pedestrianReleased = true;
            hazardStartTime = Time.time;
            speedAtHazard = speed;
            phase = "TEHLİKE: Yaya geçiyor — hızınızı güvenli seviyeye düşürün";
            SetPedestrianAnimation(1f);
        }

        if (pedestrianReleased)
        {
            MovePedestrian();

            if (reactionTime < 0f && speed <= speedAtHazard - 3f)
                reactionTime = Time.time - hazardStartTime;

            minimumSpeed = Mathf.Min(minimumSpeed, speed);
            float pedestrianDistance = HorizontalDistance(vehicle.transform.position, pedestrian.transform.position);
            closestPedestrianDistance = Mathf.Min(closestPedestrianDistance, pedestrianDistance);
            if (pedestrianDistance <= PedestrianNearMissDistance)
                nearMiss = true;
            if (pedestrianDistance <= PedestrianCollisionDistance)
                collided = true;
        }

        if (!decisionCaptured && distanceToCrossing <= safeDecisionDistance)
        {
            decisionCaptured = true;
            speedAtDecisionLine = speed;
        }

        float deceleration = (previousSpeed - speed) / Mathf.Max(Time.deltaTime, 0.0001f) / 3.6f;
        maximumDeceleration = Mathf.Max(maximumDeceleration, deceleration);
        previousSpeed = speed;

        samples.Add(new Sample
        {
            time = elapsed,
            speed = speed,
            distanceToCrossing = distanceToCrossing,
            distanceToPedestrian = HorizontalDistance(vehicle.transform.position, pedestrian.transform.position),
            pedestrianMoving = pedestrianReleased
        });

        bool passedCrossing = distanceToCrossing < -10f;
        bool timedOut = elapsed >= trialTimeoutSeconds;
        if (collided || passedCrossing || timedOut)
            FinishScenario(timedOut);
    }

    void MovePedestrian()
    {
        Vector3 next = Vector3.MoveTowards(
            pedestrian.transform.position,
            pedestrianEnd,
            pedestrianCrossingSpeed * Time.deltaTime);
        // Yüksekliği sabit tutmak, aşağı ışının yayanın kendi collider'ına çarpıp
        // karakteri her karede yukarı taşımasını önler.
        next.y = pedestrianStart.y;
        pedestrian.transform.position = next;

        if ((pedestrian.transform.position - pedestrianEnd).sqrMagnitude < 0.04f)
            SetPedestrianAnimation(0f);
    }

    void FinishScenario(bool timedOut)
    {
        finished = true;
        phase = "Senaryo tamamlandı";

        if (!decisionCaptured)
            speedAtDecisionLine = Mathf.Abs(vehicle.Speed);
        if (minimumSpeed == float.MaxValue)
            minimumSpeed = Mathf.Abs(vehicle.Speed);
        if (closestPedestrianDistance == float.MaxValue)
            closestPedestrianDistance = HorizontalDistance(vehicle.transform.position, pedestrian.transform.position);

        bool respectedLimit = speedAtDecisionLine <= speedLimitKmh + 0.5f;
        bool safeInteraction = !collided && !nearMiss;
        bool passed = respectedLimit && safeInteraction;

        if (collided)
            result = "BAŞARISIZ — Yaya ile çarpışma riski oluştu";
        else if (nearMiss)
            result = "BAŞARISIZ — Yayaya tehlikeli derecede yaklaşıldı";
        else if (!respectedLimit)
            result = "BAŞARISIZ — Güvenlik çizgisinde hız 50 km/s üzerindeydi";
        else if (timedOut)
            result = "BAŞARILI — Araç yaya geçidinden önce güvenli şekilde yavaşladı/durdu";
        else if (speedAtDecisionLine <= 30f)
            result = "MÜKEMMEL — Yaya geçidine uygun ve kontrollü hızla yaklaşıldı";
        else
            result = "BAŞARILI — Hız sınırına zamanında uyuldu";

        WriteResultFile(passed);
        Debug.Log(BuildSummary(passed));
    }

    string BuildSummary(bool passed)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "[Koşullara Uygun Olmayan Hız] {0}\n" +
            "Karar çizgisi hızı: {1:0.0} km/s | En düşük hız: {2:0.0} km/s | " +
            "Tepki süresi: {3} sn | Yayaya en yakın mesafe: {4:0.00} m | " +
            "Maks. yavaşlama: {5:0.00} m/s² | Kayıt: {6}",
            passed ? "BAŞARILI" : "BAŞARISIZ",
            speedAtDecisionLine,
            minimumSpeed,
            reactionTime < 0f ? "tespit edilmedi" : reactionTime.ToString("0.00", CultureInfo.InvariantCulture),
            closestPedestrianDistance,
            maximumDeceleration,
            resultFile);
    }

    void WriteResultFile(bool passed)
    {
        try
        {
            string directory = Path.Combine(Application.persistentDataPath, "ScenarioResults");
            Directory.CreateDirectory(directory);
            resultFile = Path.Combine(directory,
                "KosullaraUygunOlmayanHiz_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");

            var csv = new StringBuilder();
            csv.AppendLine("scenario,result,speed_limit_kmh,initial_speed_kmh,speed_at_decision_kmh,min_speed_kmh,reaction_time_s,closest_pedestrian_m,max_deceleration_ms2");
            csv.AppendFormat(CultureInfo.InvariantCulture,
                "Kosullara Uygun Olmayan Hiz,{0},{1:0.0},{2:0.0},{3:0.0},{4:0.0},{5:0.00},{6:0.00},{7:0.00}\n",
                passed ? "PASS" : "FAIL", speedLimitKmh, initialSpeedKmh, speedAtDecisionLine,
                minimumSpeed, reactionTime, closestPedestrianDistance, maximumDeceleration);
            csv.AppendLine();
            csv.AppendLine("time_s,speed_kmh,distance_to_crossing_m,distance_to_pedestrian_m,pedestrian_moving");
            foreach (var sample in samples)
            {
                csv.AppendFormat(CultureInfo.InvariantCulture, "{0:0.000},{1:0.00},{2:0.00},{3:0.00},{4}\n",
                    sample.time, sample.speed, sample.distanceToCrossing,
                    sample.distanceToPedestrian, sample.pedestrianMoving ? 1 : 0);
            }
            File.WriteAllText(resultFile, csv.ToString(), Encoding.UTF8);
        }
        catch (Exception exception)
        {
            resultFile = "Kayıt yazılamadı: " + exception.Message;
            Debug.LogWarning("[Koşullara Uygun Olmayan Hız] " + resultFile);
        }
    }

    void CreateCrosswalk()
    {
        var material = CreateMaterial(Color.white);
        for (int i = -4; i <= 4; i++)
        {
            var stripe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stripe.name = "Scenario_CrosswalkStripe";
            stripe.transform.SetParent(transform, true);
            Vector3 position = crossingCenter + approachForward * (i * 0.65f) + Vector3.up * 0.035f;
            position = PutOnGround(position, crossingCenter.y) + Vector3.up * 0.035f;
            stripe.transform.position = position;
            stripe.transform.rotation = Quaternion.LookRotation(approachForward, Vector3.up);
            stripe.transform.localScale = new Vector3(7f, 0.04f, 0.35f);
            stripe.GetComponent<Renderer>().sharedMaterial = material;
            Destroy(stripe.GetComponent<Collider>());
        }
    }

    void CreateSpeedLimitSign()
    {
        Vector3 signBase = crossingCenter - approachForward * 20f + approachRight * 4.8f;
        signBase = PutOnGround(signBase, crossingCenter.y);
        var root = new GameObject("Scenario_SpeedLimit50Sign");
        root.transform.SetParent(transform, true);

        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "Pole";
        pole.transform.SetParent(root.transform, true);
        pole.transform.position = signBase + Vector3.up * 1.25f;
        pole.transform.localScale = new Vector3(0.055f, 1.25f, 0.055f);
        pole.GetComponent<Renderer>().sharedMaterial = CreateMaterial(new Color(0.55f, 0.55f, 0.55f));
        Destroy(pole.GetComponent<Collider>());

        Quaternion discRotation = Quaternion.FromToRotation(Vector3.up, -approachForward);
        var redDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        redDisc.name = "RedBorder";
        redDisc.transform.SetParent(root.transform, true);
        redDisc.transform.position = signBase + Vector3.up * 2.65f;
        redDisc.transform.rotation = discRotation;
        redDisc.transform.localScale = new Vector3(0.6f, 0.055f, 0.6f);
        redDisc.GetComponent<Renderer>().sharedMaterial = CreateMaterial(new Color(0.8f, 0.02f, 0.02f));
        Destroy(redDisc.GetComponent<Collider>());

        var whiteDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        whiteDisc.name = "WhiteCenter";
        whiteDisc.transform.SetParent(root.transform, true);
        whiteDisc.transform.position = redDisc.transform.position - approachForward * 0.065f;
        whiteDisc.transform.rotation = discRotation;
        whiteDisc.transform.localScale = new Vector3(0.47f, 0.06f, 0.47f);
        whiteDisc.GetComponent<Renderer>().sharedMaterial = CreateMaterial(Color.white);
        Destroy(whiteDisc.GetComponent<Collider>());

        var textObject = new GameObject("SpeedText");
        textObject.transform.SetParent(root.transform, true);
        textObject.transform.position = redDisc.transform.position - approachForward * 0.14f;
        textObject.transform.rotation = Quaternion.LookRotation(-approachForward, Vector3.up);
        var text = textObject.AddComponent<TextMesh>();
        text.text = "50";
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 128;
        text.characterSize = 0.012f;
        text.color = Color.black;
    }

    Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Diffuse");
        var material = new Material(shader);
        material.color = color;
        return material;
    }

    Vector3 PutOnGround(Vector3 position, float fallbackY)
    {
        RaycastHit hit;
        Vector3 origin = new Vector3(position.x, Mathf.Max(position.y, fallbackY) + 8f, position.z);
        if (Physics.Raycast(origin, Vector3.down, out hit, 20f, ~0, QueryTriggerInteraction.Ignore))
            position.y = hit.point.y;
        else
            position.y = fallbackY;
        return position;
    }

    void SetPedestrianAnimation(float speed)
    {
        if (pedestrianAnimator != null && pedestrianAnimator.enabled)
            pedestrianAnimator.SetFloat("Speed", speed);
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    public void SetParameter(NetworkingManager.ExperimentParameter[] parameters)
    {
        if (parameters == null)
            return;

        foreach (var parameter in parameters)
        {
            float value;
            if (!float.TryParse(parameter.value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                continue;

            switch (parameter.name.Trim().ToLowerInvariant())
            {
                case "speed_limit": speedLimitKmh = Mathf.Max(1f, value); break;
                case "initial_speed": initialSpeedKmh = Mathf.Max(1f, value); break;
                case "crossing_distance": crossingDistance = Mathf.Max(20f, value); break;
                case "pedestrian_trigger_distance": pedestrianTriggerDistance = Mathf.Max(5f, value); break;
                case "safe_decision_distance": safeDecisionDistance = Mathf.Max(2f, value); break;
                case "pedestrian_speed": pedestrianCrossingSpeed = Mathf.Max(0.2f, value); break;
            }
        }
    }

    void OnGUI()
    {
        if (NetworkingManager.Instance != null && NetworkingManager.Instance.hideGui)
            return;

        EnsureGuiStyles();
        float width = Mathf.Min(560f, Screen.width - 30f);
        GUILayout.BeginArea(new Rect(15f, 15f, width, finished ? 235f : 200f), panelStyle);
        GUILayout.Label("SENARYO 1 — KOŞULLARA UYGUN OLMAYAN HIZ", titleStyle);
        GUILayout.Label("Hız sınırı: " + speedLimitKmh.ToString("0") + " km/s", valueStyle);
        if (vehicle != null)
            GUILayout.Label("Anlık hız: " + Mathf.Abs(vehicle.Speed).ToString("0.0") + " km/s", valueStyle);
        GUILayout.Label(phase, pedestrianReleased && !finished ? warningStyle : valueStyle);
        if (finished)
        {
            GUILayout.Space(8f);
            GUILayout.Label(result, result.StartsWith("BAŞARISIZ") ? warningStyle : titleStyle);
            GUILayout.Label("Karar çizgisi hızı: " + speedAtDecisionLine.ToString("0.0") + " km/s", valueStyle);
        }
        GUILayout.EndArea();
    }

    void EnsureGuiStyles()
    {
        if (panelStyle != null)
            return;

        panelStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(18, 18, 14, 14),
            alignment = TextAnchor.UpperLeft
        };
        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            normal = { textColor = Color.white }
        };
        warningStyle = new GUIStyle(valueStyle)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(1f, 0.35f, 0.2f) }
        };
    }
}
