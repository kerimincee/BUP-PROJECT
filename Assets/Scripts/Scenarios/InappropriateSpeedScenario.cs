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
/// Aracı tamamen sürücü kontrolünde bırakır, kontrollü bir yaya geçişi oluşturur ve
/// sürücünün tehlikeye yaklaşırken 50 km/s sınırına zamanında uyup uymadığını kaydeder.
/// </summary>
public class InappropriateSpeedScenario : MonoBehaviour, IExperimentModifier
{
    [Header("Scenario configuration")]
    [SerializeField] float speedLimitKmh = 50f;
    [SerializeField] float eventPlacementDistance = 45f;
    [SerializeField] float eventArmSpeedKmh = 45f;
    [SerializeField] float fallbackArmTravelDistance = 55f;
    [SerializeField] float pedestrianTimeToArrival = 3.2f;
    [SerializeField] float safeDecisionDistance = 18f;
    [SerializeField] float pedestrianLateralOffset = 4.5f;
    [SerializeField] float pedestrianCrossingSpeed = 1.4f;
    [SerializeField] float trialTimeoutSeconds = 120f;

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
    bool drivingStarted;
    bool eventArmed;
    bool pedestrianReleased;
    bool decisionCaptured;
    bool finished;
    bool collided;
    bool nearMiss;
    float scenarioStartTime;
    float drivingStartTime;
    float hazardStartTime;
    float reactionTime = -1f;
    float speedAtHazard;
    float speedAtDecisionLine = -1f;
    float minimumSpeed = float.MaxValue;
    float closestPedestrianDistance = float.MaxValue;
    float previousSpeed;
    float maximumDeceleration;
    float maximumSpeed;
    float overspeedDuration;
    float stoppedSafelySince = -1f;
    float distanceTravelled;
    Vector3 previousVehiclePosition;
    float activePedestrianSpeed;
    float closestCrossingDistance = float.MaxValue;
    bool approachedCrossing;
    string phase = "Hazırlanıyor";
    string result = "Değerlendirme sürüyor";
    string resultFile = "";
    readonly List<Sample> samples = new List<Sample>(2048);

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

        SetPedestrianVisible(false);
        SetPedestrianAnimation(0f);

        // Araç hızına müdahale edilmez. Sürücü gaz ve frenle doğal biçimde kullanır.
        previousSpeed = CurrentSpeedKmh();
        previousVehiclePosition = vehicle.transform.position;
        scenarioStartTime = Time.time;
        phase = "Aracı normal şekilde kullanın — şehir içi hız sınırı 50 km/s";
        initialized = true;

        Debug.Log("[Koşullara Uygun Olmayan Hız] Senaryo başladı. Araç hızı sürücü kontrolündedir; limit 50 km/s.");
    }

    void Update()
    {
        if (!initialized || finished)
            return;

        float speed = CurrentSpeedKmh();
        float frameTravel = HorizontalDistance(vehicle.transform.position, previousVehiclePosition);
        if (frameTravel < 5f)
            distanceTravelled += frameTravel;
        previousVehiclePosition = vehicle.transform.position;

        if (!drivingStarted && speed >= 3f)
        {
            drivingStarted = true;
            drivingStartTime = Time.time;
        }

        float elapsed = drivingStarted ? Time.time - drivingStartTime : 0f;
        maximumSpeed = Mathf.Max(maximumSpeed, speed);
        if (speed > speedLimitKmh)
            overspeedDuration += Time.deltaTime;

        if (!eventArmed)
        {
            if (speed > speedLimitKmh + 5f)
                phase = "HIZ SINIRI AŞILDI — Hızınızı güvenli seviyeye düşürün";
            else if (speed > speedLimitKmh)
                phase = "UYARI — 50 km/s hız sınırının üzerindesiniz";
            else if (drivingStarted)
                phase = "Hız uygun — yolu ve çevreyi gözlemleyin";

            bool speedCondition = speed >= eventArmSpeedKmh && distanceTravelled >= 15f;
            bool travelFallback = distanceTravelled >= fallbackArmTravelDistance;
            if (speedCondition || travelFallback)
                ArmPedestrianEvent();

            samples.Add(new Sample
            {
                time = elapsed,
                speed = speed,
                distanceToCrossing = -1f,
                distanceToPedestrian = -1f,
                pedestrianMoving = false
            });
            previousSpeed = speed;
            return;
        }

        float displayDistance = HorizontalDistance(crossingCenter, vehicle.transform.position);
        float dynamicTriggerDistance = Mathf.Clamp((speed / 3.6f) * pedestrianTimeToArrival, 26f, 52f);
        closestCrossingDistance = Mathf.Min(closestCrossingDistance, displayDistance);
        if (displayDistance <= 15f)
            approachedCrossing = true;

        if (!pedestrianReleased && displayDistance <= dynamicTriggerDistance)
        {
            pedestrianReleased = true;
            hazardStartTime = Time.time;
            speedAtHazard = speed;
            float estimatedArrival = displayDistance / Mathf.Max(speed / 3.6f, 0.5f);
            activePedestrianSpeed = Mathf.Clamp(
                pedestrianLateralOffset / Mathf.Max(estimatedArrival, 1.5f),
                pedestrianCrossingSpeed * 0.8f,
                pedestrianCrossingSpeed * 1.6f);
            phase = "TEHLİKE — Yaya geçitte, kontrollü biçimde yavaşlayın";
            SetPedestrianAnimation(activePedestrianSpeed / 1.4f);
        }

        if (pedestrianReleased)
        {
            MovePedestrian();

            if (reactionTime < 0f && (vehicle.breaking > 0.1f || speed <= speedAtHazard - 3f))
                reactionTime = Time.time - hazardStartTime;

            minimumSpeed = Mathf.Min(minimumSpeed, speed);
            float pedestrianDistance = HorizontalDistance(vehicle.transform.position, pedestrian.transform.position);
            closestPedestrianDistance = Mathf.Min(closestPedestrianDistance, pedestrianDistance);
            if (pedestrianDistance <= PedestrianNearMissDistance)
                nearMiss = true;
            if (pedestrianDistance <= PedestrianCollisionDistance)
                collided = true;
        }

        if (!decisionCaptured && displayDistance <= safeDecisionDistance)
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
            distanceToCrossing = displayDistance,
            distanceToPedestrian = HorizontalDistance(vehicle.transform.position, pedestrian.transform.position),
            pedestrianMoving = pedestrianReleased
        });

        bool passedCrossing = approachedCrossing && displayDistance > closestCrossingDistance + 4f;
        if (pedestrianReleased && !passedCrossing && speed < 0.5f && displayDistance > 2f && displayDistance < 52f)
        {
            if (stoppedSafelySince < 0f)
                stoppedSafelySince = Time.time;
        }
        else
        {
            stoppedSafelySince = -1f;
        }

        bool safeStopCompleted = stoppedSafelySince > 0f && Time.time - stoppedSafelySince >= 3f;
        bool timedOut = drivingStarted && elapsed >= trialTimeoutSeconds;
        if (collided || passedCrossing || safeStopCompleted || timedOut)
            FinishScenario(timedOut);
    }

    void ArmPedestrianEvent()
    {
        eventArmed = true;
        approachForward = Vector3.ProjectOnPlane(vehicle.transform.forward, Vector3.up).normalized;
        approachRight = Vector3.Cross(Vector3.up, approachForward).normalized;

        crossingCenter = vehicle.transform.position + approachForward * eventPlacementDistance;
        crossingCenter = PutOnGround(crossingCenter, vehicle.transform.position.y);
        pedestrianStart = PutOnGround(crossingCenter + approachRight * pedestrianLateralOffset, crossingCenter.y);
        pedestrianEnd = PutOnGround(crossingCenter - approachRight * pedestrianLateralOffset, crossingCenter.y);

        pedestrian.transform.position = pedestrianStart;
        pedestrian.transform.rotation = Quaternion.LookRotation((pedestrianEnd - pedestrianStart).normalized, Vector3.up);
        SetPedestrianVisible(true);
        SetPedestrianAnimation(0f);
        CreatePedestrianMarker();
        CreateCrosswalk();
        CreateSpeedLimitSign();
        phase = "DİKKAT — Yaya kaldırımda, geçmeye hazırlanıyor";

        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[Koşullara Uygun Olmayan Hız] Yaya olayı kuruldu. Hız: {0:0.0} km/s, mesafe: {1:0.0} m.",
            CurrentSpeedKmh(), eventPlacementDistance));
    }

    void MovePedestrian()
    {
        Vector3 next = Vector3.MoveTowards(
            pedestrian.transform.position,
            pedestrianEnd,
            activePedestrianSpeed * Time.deltaTime);
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
            speedAtDecisionLine = CurrentSpeedKmh();
        if (minimumSpeed == float.MaxValue)
            minimumSpeed = CurrentSpeedKmh();
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
            "Karar çizgisi hızı: {1:0.0} km/s | En yüksek hız: {2:0.0} km/s | En düşük hız: {3:0.0} km/s | " +
            "Tepki süresi: {4} sn | Limit üstü süre: {5:0.00} sn | Yayaya en yakın mesafe: {6:0.00} m | " +
            "Maks. yavaşlama: {7:0.00} m/s² | Kayıt: {8}",
            passed ? "BAŞARILI" : "BAŞARISIZ",
            speedAtDecisionLine,
            maximumSpeed,
            minimumSpeed,
            reactionTime < 0f ? "tespit edilmedi" : reactionTime.ToString("0.00", CultureInfo.InvariantCulture),
            overspeedDuration,
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
            csv.AppendLine("scenario,result,speed_limit_kmh,max_speed_kmh,overspeed_duration_s,speed_at_decision_kmh,min_speed_kmh,reaction_time_s,closest_pedestrian_m,max_deceleration_ms2");
            csv.AppendFormat(CultureInfo.InvariantCulture,
                "Kosullara Uygun Olmayan Hiz,{0},{1:0.0},{2:0.0},{3:0.00},{4:0.0},{5:0.0},{6:0.00},{7:0.00},{8:0.00}\n",
                passed ? "PASS" : "FAIL", speedLimitKmh, maximumSpeed, overspeedDuration,
                speedAtDecisionLine, minimumSpeed, reactionTime, closestPedestrianDistance, maximumDeceleration);
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

    void CreatePedestrianMarker()
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "Scenario_PedestrianMarker";
        marker.transform.SetParent(pedestrian.transform, false);
        marker.transform.localPosition = new Vector3(0f, 2.25f, 0f);
        marker.transform.localScale = Vector3.one * 0.22f;
        var material = CreateMaterial(new Color(1f, 0.68f, 0.05f));
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", new Color(0.55f, 0.22f, 0.01f));
        }
        marker.GetComponent<Renderer>().sharedMaterial = material;
        Destroy(marker.GetComponent<Collider>());
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
        // Yol seviyesinin hemen üstünden tarama yapılır. Yüksekten tarama, viraj
        // yakınındaki bina/saçak yüzeyini zemin sanarak yayayı çatıya taşıyabiliyordu.
        Vector3 origin = new Vector3(position.x, fallbackY + 2.5f, position.z);
        if (Physics.Raycast(origin, Vector3.down, out hit, 5f, ~0, QueryTriggerInteraction.Ignore))
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

    void SetPedestrianVisible(bool visible)
    {
        if (pedestrian == null)
            return;
        foreach (var renderer in pedestrian.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = visible;
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    float CurrentSpeedKmh()
    {
        if (vehicleBody == null)
            return 0f;
        Vector3 horizontalVelocity = Vector3.ProjectOnPlane(vehicleBody.velocity, Vector3.up);
        return horizontalVelocity.magnitude * 3.6f;
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
                case "event_placement_distance": eventPlacementDistance = Mathf.Clamp(value, 30f, 60f); break;
                case "event_arm_speed": eventArmSpeedKmh = Mathf.Max(10f, value); break;
                case "pedestrian_tta": pedestrianTimeToArrival = Mathf.Clamp(value, 2f, 6f); break;
                case "safe_decision_distance": safeDecisionDistance = Mathf.Max(2f, value); break;
                case "pedestrian_speed": pedestrianCrossingSpeed = Mathf.Max(0.2f, value); break;
            }
        }
    }

    void OnGUI()
    {
        if (NetworkingManager.Instance != null && NetworkingManager.Instance.hideGui)
            return;

        ProfessionalGUI.Ensure();
        float width = Mathf.Min(375f, Screen.width - 30f);
        float height = finished ? 330f : 270f;
        float x = Mathf.Max(15f, Screen.width - width - 20f);
        GUILayout.BeginArea(new Rect(x, 20f, width, height), ProfessionalGUI.Panel);
        GUILayout.Label("Koşullara Uygun Olmayan Hız", ProfessionalGUI.Header);

        float speed = CurrentSpeedKmh();
        Color speedColor = speed <= speedLimitKmh
            ? new Color(0.20f, 0.86f, 0.56f)
            : speed <= speedLimitKmh + 5f
                ? new Color(1f, 0.72f, 0.20f)
                : new Color(1f, 0.25f, 0.18f);
        var speedStyle = new GUIStyle(ProfessionalGUI.Speed);
        speedStyle.normal.textColor = speedColor;
        GUILayout.Label(speed.ToString("0") + " km/s", speedStyle);
        ProfessionalGUI.DrawProgress(speed / 80f, speedColor, 7f);

        GUILayout.BeginHorizontal();
        GUILayout.Label("HIZ SINIRI", ProfessionalGUI.Muted, GUILayout.Width(150f));
        GUILayout.FlexibleSpace();
        GUILayout.Label(speedLimitKmh.ToString("0") + " km/s", ProfessionalGUI.Label);
        GUILayout.EndHorizontal();

        if (vehicle != null && eventArmed)
        {
            float distance = HorizontalDistance(crossingCenter, vehicle.transform.position);
            GUILayout.BeginHorizontal();
            GUILayout.Label("YAYA GEÇİDİ", ProfessionalGUI.Muted, GUILayout.Width(150f));
            GUILayout.FlexibleSpace();
            GUILayout.Label(distance.ToString("0") + " m", ProfessionalGUI.Label);
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("YAYA OLAYI", ProfessionalGUI.Muted, GUILayout.Width(150f));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Hazırlanıyor", ProfessionalGUI.Label);
            GUILayout.EndHorizontal();
        }

        var statusStyle = new GUIStyle(ProfessionalGUI.Badge);
        statusStyle.normal.textColor = speedColor;
        if (pedestrianReleased && !finished)
            statusStyle.normal.textColor = new Color(1f, 0.30f, 0.18f);
        GUILayout.Space(8f);
        GUILayout.Label(phase, statusStyle);

        if (!drivingStarted && initialized)
            GUILayout.Label("Gaz ve fren tamamen sürücünün kontrolündedir.", ProfessionalGUI.Muted);

        if (finished)
        {
            GUILayout.Space(8f);
            var resultStyle = new GUIStyle(ProfessionalGUI.Badge);
            resultStyle.normal.textColor = result.StartsWith("BAŞARISIZ")
                ? new Color(1f, 0.25f, 0.18f)
                : new Color(0.20f, 0.86f, 0.56f);
            GUILayout.Label(result, resultStyle);
            GUILayout.Label("Karar çizgisi: " + speedAtDecisionLine.ToString("0.0") + " km/s  •  En yüksek: " + maximumSpeed.ToString("0.0") + " km/s", ProfessionalGUI.Label);
        }
        GUILayout.EndArea();
    }
}
