using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace RearMirrorCruiser;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "itsjustliliana.rearmirrorcruiser";
    public const string PluginName = "Rear Mirror Cruiser";
    public const string PluginVersion = "1.0.10";

    private void Awake()
    {
        var controllerObject = new GameObject("RearMirrorCruiserController");
        DontDestroyOnLoad(controllerObject);
        controllerObject.hideFlags = HideFlags.HideAndDontSave;

        var controller = controllerObject.AddComponent<RearMirrorController>();
        controller.Initialize(Config, Logger);

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
    }
}

public sealed class RearMirrorController : MonoBehaviour
{
    private ConfigEntry<bool>? _enableRearFeed;
    private ConfigEntry<bool>? _enableInteriorFeed;
    private ConfigEntry<float>? _feedOpacity;
    private ConfigEntry<float>? _cameraFieldOfView;
    private ConfigEntry<float>? _rearCameraFps;
    private ConfigEntry<float>? _interiorCameraFps;

    private const int RenderWidth = 320;
    private const int RenderHeight = 180;
    private const float DefaultFeedOpacity = 0.95f;
    private const float DefaultRearCameraFieldOfView = 62f;
    private const float RearCameraFarClip = 60f;

    private const float RearCameraBack = -3.9f;
    private const float RearCameraHeight = 2.2f;
    private const float RearCameraTilt = 18f;

    private const float InteriorRearCameraBack = 0.65f;
    private const float InteriorRearCameraHeight = 1.55f;
    private const float InteriorRearCameraTilt = 22f;

    private const float DefaultRearCameraFps = 12f;
    private const float DefaultInteriorCameraFps = 8f;

    private const float InteriorFeedWidth = 230f;
    private const float InteriorFeedHeight = 98f;
    private const float InteriorFeedGap = 14f;
    private const float RearFeedWidth = 520f;
    private const float RearFeedHeight = 165f;
    private const float FeedPosY = -18f;
    private const float RearFeedPosX = 0f;
    private const float InteriorFeedPosX = RearFeedPosX + (RearFeedWidth * 0.5f) + InteriorFeedGap + (InteriorFeedWidth * 0.5f);

    private BepInEx.Logging.ManualLogSource? _logger;

    private Canvas? _canvas;
    private RawImage? _feedImage;
    private RawImage? _interiorFeedImage;

    private RenderTexture? _renderTexture;
    private RenderTexture? _interiorRenderTexture;

    private Camera? _rearCamera;
    private Camera? _interiorCamera;

    private GameObject? _rearCameraObject;
    private GameObject? _interiorCameraObject;

    private bool _feedVisible;
    private float _lastDrivingTime;
    private Transform? _currentVehicleTransform;
    private bool _forceRenderThisFrame;
    private float _nextRearRenderAt;
    private float _nextInteriorRenderAt;

    private Type? _vehicleControllerType;

    private readonly BindingFlags _instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public void Initialize(ConfigFile config, BepInEx.Logging.ManualLogSource logger)
    {
        _logger = logger;

        _enableRearFeed = config.Bind("General", "EnableRearMirrorFeed", true, "Enable the rear mirror feed.");
        _enableInteriorFeed = config.Bind("General", "EnableInsideViewFeed", true, "Enable the inside/interior view feed.");
        _feedOpacity = config.Bind("UI", "FeedOpacity", DefaultFeedOpacity, "Feed opacity (0.0 to 1.0).");
        _cameraFieldOfView = config.Bind("Camera", "FieldOfView", DefaultRearCameraFieldOfView, "Rear camera field of view.");
        _rearCameraFps = config.Bind("Performance", "RearCameraFPS", DefaultRearCameraFps, "Rear camera render rate (frames per second).");
        _interiorCameraFps = config.Bind("Performance", "InteriorCameraFPS", DefaultInteriorCameraFps, "Interior camera render rate (frames per second).");

        BuildUi();
    }

    private void Update()
    {
        var rearEnabled = _enableRearFeed?.Value ?? true;
        var interiorEnabled = _enableInteriorFeed?.Value ?? true;
        if (!rearEnabled && !interiorEnabled)
        {
            _currentVehicleTransform = null;
            SetFeedVisible(false);
            return;
        }

        var localPlayer = TryGetLocalPlayer();
        if (localPlayer == null)
        {
            SetFeedVisible(false);
            return;
        }

        var state = EvaluateDriverState(localPlayer);

        var vehicleTransform = GetStableVehicleTransform(state.VehicleTransform);
        if (state.IsInVehicle && state.IsDriver && vehicleTransform != null)
        {
            _lastDrivingTime = Time.unscaledTime;

            if (_currentVehicleTransform != vehicleTransform)
            {
                _currentVehicleTransform = vehicleTransform;

                if (rearEnabled)
                {
                    EnsureRearCamera(vehicleTransform);
                }

                if (interiorEnabled)
                {
                    EnsureInteriorCamera(vehicleTransform);
                }

                _logger?.LogInfo($"Cameras initialized for vehicle: {vehicleTransform.name}");
            }
            else
            {
                if (rearEnabled && (_rearCameraObject == null || _rearCamera == null || _rearCameraObject.transform.parent != vehicleTransform))
                {
                    EnsureRearCamera(vehicleTransform);
                }

                if (interiorEnabled && (_interiorCameraObject == null || _interiorCamera == null || _interiorCameraObject.transform.parent != vehicleTransform))
                {
                    EnsureInteriorCamera(vehicleTransform);
                }
            }

            ApplyFeedPresentation(rearEnabled, interiorEnabled);

            SetFeedVisible(true);
            return;
        }

        if (Time.unscaledTime - _lastDrivingTime < 0.2f)
        {
            return;
        }

        _currentVehicleTransform = null;
        SetFeedVisible(false);
    }

    private void LateUpdate()
    {
        if (!_feedVisible)
        {
            return;
        }

        var now = Time.unscaledTime;
        var forceRender = _forceRenderThisFrame;
        var rearEnabled = _enableRearFeed?.Value ?? true;
        var interiorEnabled = _enableInteriorFeed?.Value ?? true;
        if (forceRender)
        {
            _forceRenderThisFrame = false;
        }

        var configuredFov = GetConfiguredRearFov();
        if (_rearCamera != null)
        {
            _rearCamera.fieldOfView = configuredFov;
        }
        if (_interiorCamera != null)
        {
            _interiorCamera.fieldOfView = Mathf.Clamp(configuredFov - 6f, 35f, 110f);
        }

        var rearFps = _rearCameraFps?.Value ?? DefaultRearCameraFps;
        var shouldRenderRear = ShouldRenderCamera(now, rearFps, ref _nextRearRenderAt, forceRender);
        if (rearEnabled && shouldRenderRear && _rearCamera != null && _renderTexture != null)
        {
            if (_rearCamera.targetTexture != _renderTexture)
            {
                _rearCamera.targetTexture = _renderTexture;
            }
            _rearCamera.Render();
        }
        else if (!rearEnabled)
        {
            _nextRearRenderAt = 0f;
        }

        var interiorFps = _interiorCameraFps?.Value ?? DefaultInteriorCameraFps;
        var shouldRenderInterior = ShouldRenderCamera(now, interiorFps, ref _nextInteriorRenderAt, forceRender);
        if (interiorEnabled && shouldRenderInterior && _interiorCamera != null && _interiorRenderTexture != null)
        {
            if (_interiorCamera.targetTexture != _interiorRenderTexture)
            {
                _interiorCamera.targetTexture = _interiorRenderTexture;
            }
            _interiorCamera.Render();
        }
        else if (!interiorEnabled)
        {
            _nextInteriorRenderAt = 0f;
        }
    }

    private void OnDestroy()
    {
        if (_renderTexture != null)
        {
            _renderTexture.Release();
            Destroy(_renderTexture);
            _renderTexture = null;
        }

        if (_interiorRenderTexture != null)
        {
            _interiorRenderTexture.Release();
            Destroy(_interiorRenderTexture);
            _interiorRenderTexture = null;
        }

        if (_rearCameraObject != null)
        {
            Destroy(_rearCameraObject);
            _rearCameraObject = null;
            _rearCamera = null;
        }

        if (_interiorCameraObject != null)
        {
            Destroy(_interiorCameraObject);
            _interiorCameraObject = null;
            _interiorCamera = null;
        }

        if (_canvas != null)
        {
            Destroy(_canvas.gameObject);
            _canvas = null;
            _feedImage = null;
            _interiorFeedImage = null;
        }
    }

    private void BuildUi()
    {
        var canvasObject = new GameObject("RearMirrorCanvas");
        DontDestroyOnLoad(canvasObject);
        canvasObject.hideFlags = HideFlags.HideAndDontSave;

        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = short.MaxValue;
        canvasObject.AddComponent<CanvasScaler>();
        canvasObject.AddComponent<GraphicRaycaster>();

        var rearObject = new GameObject("RearMirrorFeed");
        rearObject.transform.SetParent(canvasObject.transform, false);
        rearObject.SetActive(true);

        _feedImage = rearObject.AddComponent<RawImage>();
        ConfigureFeedImage(_feedImage, RearFeedPosX, FeedPosY, RearFeedWidth, RearFeedHeight);

        var interiorObject = new GameObject("InteriorMirrorFeed");
        interiorObject.transform.SetParent(canvasObject.transform, false);
        interiorObject.SetActive(true);

        _interiorFeedImage = interiorObject.AddComponent<RawImage>();
        ConfigureFeedImage(_interiorFeedImage, InteriorFeedPosX, FeedPosY, InteriorFeedWidth, InteriorFeedHeight);

        _canvas.gameObject.SetActive(false);
    }

    private void EnsureRearCamera(Transform vehicleTransform)
    {
        if (_rearCameraObject == null)
        {
            _rearCameraObject = new GameObject("RearMirrorCamera");
            _rearCameraObject.hideFlags = HideFlags.HideAndDontSave;
            _rearCamera = _rearCameraObject.AddComponent<Camera>();
            _rearCamera.enabled = false;
            _rearCamera.clearFlags = CameraClearFlags.SolidColor;
            _rearCamera.backgroundColor = Color.black;
            _rearCamera.depth = -100f;
            _rearCamera.forceIntoRenderTexture = true;
            _rearCamera.allowHDR = false;
            _rearCamera.allowMSAA = false;
            _rearCamera.useOcclusionCulling = false;
        }

        if (_rearCameraObject.transform.parent != vehicleTransform)
        {
            _rearCameraObject.transform.SetParent(vehicleTransform, false);
        }

        _rearCameraObject.transform.localPosition = new Vector3(0f, RearCameraHeight, RearCameraBack);
        _rearCameraObject.transform.localRotation = Quaternion.Euler(RearCameraTilt, 180f, 0f);

        EnsureRenderTextures();
        ConfigureMirrorCamera(_rearCamera, _renderTexture, GetConfiguredRearFov());

    }

    private void EnsureInteriorCamera(Transform vehicleTransform)
    {
        if (_interiorCameraObject == null)
        {
            _interiorCameraObject = new GameObject("InteriorMirrorCamera");
            _interiorCameraObject.hideFlags = HideFlags.HideAndDontSave;
            _interiorCamera = _interiorCameraObject.AddComponent<Camera>();
            _interiorCamera.enabled = false;
            _interiorCamera.clearFlags = CameraClearFlags.SolidColor;
            _interiorCamera.backgroundColor = Color.black;
            _interiorCamera.depth = -100f;
            _interiorCamera.forceIntoRenderTexture = true;
            _interiorCamera.allowHDR = false;
            _interiorCamera.allowMSAA = false;
            _interiorCamera.useOcclusionCulling = false;
        }

        if (_interiorCameraObject.transform.parent != vehicleTransform)
        {
            _interiorCameraObject.transform.SetParent(vehicleTransform, false);
        }

        _interiorCameraObject.transform.localPosition = new Vector3(0f, InteriorRearCameraHeight, InteriorRearCameraBack);
        _interiorCameraObject.transform.localRotation = Quaternion.Euler(InteriorRearCameraTilt, 180f, 0f);

        EnsureRenderTextures();
        ConfigureMirrorCamera(_interiorCamera, _interiorRenderTexture, Mathf.Clamp(GetConfiguredRearFov() - 6f, 35f, 110f));
    }

    private void EnsureRenderTextures()
    {
        var width = Mathf.Clamp(RenderWidth, 128, 1024);
        var height = Mathf.Clamp(RenderHeight, 72, 1024);

        var mustRecreateRear = _renderTexture == null || _renderTexture.width != width || _renderTexture.height != height;
        if (mustRecreateRear)
        {
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
            }

            _renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.Default)
            {
                name = "RearMirrorRT",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _renderTexture.Create();
            _renderTexture.DiscardContents();
        }

        var interiorWidth = Mathf.Clamp((int)(width * 0.6f), 96, 768);
        var interiorHeight = Mathf.Clamp((int)(height * 0.6f), 54, 432);
        var mustRecreateInterior = _interiorRenderTexture == null
                                  || _interiorRenderTexture.width != interiorWidth
                                  || _interiorRenderTexture.height != interiorHeight;

        if (mustRecreateInterior)
        {
            if (_interiorRenderTexture != null)
            {
                _interiorRenderTexture.Release();
                Destroy(_interiorRenderTexture);
            }

            _interiorRenderTexture = new RenderTexture(interiorWidth, interiorHeight, 16, RenderTextureFormat.Default)
            {
                name = "InteriorMirrorRT",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _interiorRenderTexture.Create();
            _interiorRenderTexture.DiscardContents();
        }

        if (_feedImage != null && _feedImage.texture != _renderTexture)
        {
            _feedImage.texture = _renderTexture;
        }

        if (_interiorFeedImage != null && _interiorFeedImage.texture != _interiorRenderTexture)
        {
            _interiorFeedImage.texture = _interiorRenderTexture;
        }
    }

    private void SetFeedVisible(bool visible)
    {
        if (_feedVisible == visible)
        {
            return;
        }

        _feedVisible = visible;

        if (visible)
        {
            EnsureRenderTextures();

            var rearEnabled = _enableRearFeed?.Value ?? true;
            var interiorEnabled = _enableInteriorFeed?.Value ?? true;
            ApplyFeedPresentation(rearEnabled, interiorEnabled);

            if (_canvas != null && _canvas.gameObject.activeSelf)
            {
                Canvas.ForceUpdateCanvases();
            }

            var now = Time.unscaledTime;
            _nextRearRenderAt = now;
            _nextInteriorRenderAt = now;

            _forceRenderThisFrame = true;
        }
        else
        {
            _forceRenderThisFrame = false;
            _nextRearRenderAt = 0f;
            _nextInteriorRenderAt = 0f;

            var deinitializedVehicleName = _rearCameraObject?.transform.parent?.name
                                        ?? _interiorCameraObject?.transform.parent?.name;
            if (!string.IsNullOrEmpty(deinitializedVehicleName))
            {
                _logger?.LogInfo($"Cameras deinitialized for vehicle: {deinitializedVehicleName}");
            }

            if (_canvas != null)
            {
                _canvas.gameObject.SetActive(false);
            }

            if (_rearCameraObject != null)
            {
                _rearCameraObject.transform.SetParent(null);
            }

            if (_interiorCameraObject != null)
            {
                _interiorCameraObject.transform.SetParent(null);
            }
        }
    }

    private float GetConfiguredRearFov()
    {
        return Mathf.Clamp(_cameraFieldOfView?.Value ?? DefaultRearCameraFieldOfView, 40f, 120f);
    }

    private void ApplyFeedPresentation(bool rearEnabled, bool interiorEnabled)
    {
        var feedOpacity = Mathf.Clamp01(_feedOpacity?.Value ?? DefaultFeedOpacity);

        if (_feedImage != null)
        {
            _feedImage.color = new Color(1f, 1f, 1f, feedOpacity);
            _feedImage.gameObject.SetActive(rearEnabled);
        }

        if (_interiorFeedImage != null)
        {
            _interiorFeedImage.color = new Color(1f, 1f, 1f, feedOpacity);
            _interiorFeedImage.gameObject.SetActive(interiorEnabled);
        }

        if (_canvas != null)
        {
            _canvas.gameObject.SetActive(rearEnabled || interiorEnabled);
        }
    }

    private static bool ShouldRenderCamera(float now, float fps, ref float nextRenderAt, bool forceRender)
    {
        var clampedFps = Mathf.Clamp(fps, 1f, 60f);
        var interval = 1f / clampedFps;

        if (forceRender)
        {
            nextRenderAt = now + interval;
            return true;
        }

        if (nextRenderAt <= 0f)
        {
            nextRenderAt = now;
        }

        if (now + 0.0001f < nextRenderAt)
        {
            return false;
        }

        nextRenderAt = now + interval;
        return true;
    }

    private object? TryGetLocalPlayer()
    {
        var manager = GetSingletonInstance("GameNetworkManager");
        var player = GetMemberValue(manager, "localPlayerController");
        if (player != null)
        {
            return player;
        }

        var startOfRound = GetSingletonInstance("StartOfRound");
        return GetMemberValue(startOfRound, "localPlayerController");
    }

    private DriverState EvaluateDriverState(object? localPlayer)
    {
        var playerType = localPlayer?.GetType();

        var attachedVehicle = localPlayer != null && playerType != null
            ? GetMemberValue(localPlayer, "attachedVehicle")
            : null;

        var localDrivenVehicle = FindLocalDrivenVehicle(localPlayer);
        var vehicleRef = localDrivenVehicle ?? attachedVehicle;
        var vehicleReportsLocalDriver = vehicleRef != null && IsVehicleDrivenByLocalPlayer(vehicleRef, localPlayer);

        var inVehicleByPlayer = localPlayer != null && playerType != null
            ? ReadNamedBool(localPlayer, playerType, new[] { "invehicleanimation", "invehicle", "isinvehicle" })
            : null;

        var isInVehicle = inVehicleByPlayer ?? (vehicleRef != null) || vehicleReportsLocalDriver;

        var isDriver = vehicleReportsLocalDriver;
        if (!isDriver && localDrivenVehicle != null)
        {
            isDriver = true;
        }
        else if (!isDriver && localPlayer != null && playerType != null)
        {
            var isDriverValue = ReadNamedBool(localPlayer, playerType, new[] { "isdriver", "driving", "localplayerincontrol" });
            var seatIndex = ReadNamedInt(localPlayer, playerType, new[] { "vehicleseat", "seatindex", "seat" });
            isDriver = isDriverValue ?? (seatIndex.HasValue && seatIndex.Value == 0);
        }

        return new DriverState
        {
            IsInVehicle = isInVehicle,
            IsDriver = isInVehicle && isDriver,
            VehicleTransform = ResolveTransform(vehicleRef)
        };
    }

    private object? FindLocalDrivenVehicle(object? localPlayer)
    {
        _vehicleControllerType ??= FindTypeByName("VehicleController");
        if (_vehicleControllerType == null)
        {
            return null;
        }

        var objects = Resources.FindObjectsOfTypeAll(_vehicleControllerType);
        foreach (var obj in objects)
        {
            if (obj is not Component component)
            {
                continue;
            }

            if (!component.gameObject.scene.IsValid())
            {
                continue;
            }

            if (IsVehicleDrivenByLocalPlayer(component, localPlayer))
            {
                return component;
            }
        }

        return null;
    }

    private bool IsVehicleDrivenByLocalPlayer(object vehicle, object? localPlayer)
    {
        var vehicleType = vehicle.GetType();

        var isLocalDriver = GetBoolMemberValue(vehicle, vehicleType, "isLocalDriver");
        if (isLocalDriver == true)
        {
            return true;
        }

        var localPlayerInControl = GetBoolMemberValue(vehicle, vehicleType, "localPlayerInControl");
        var localPlayerInPassengerSeat = GetBoolMemberValue(vehicle, vehicleType, "localPlayerInPassengerSeat");
        if (localPlayerInControl == true && localPlayerInPassengerSeat != true)
        {
            return true;
        }

        var currentDriver = GetMemberValue(vehicle, "currentDriver");
        if (localPlayer != null && currentDriver != null && ReferenceEquals(currentDriver, localPlayer))
        {
            return true;
        }

        var driverId = GetNumberMemberValue(vehicle, vehicleType, "driverId");
        var localPlayerId = localPlayer != null ? GetNumberMemberValue(localPlayer, localPlayer.GetType(), "playerClientId") : null;
        if (driverId.HasValue && localPlayerId.HasValue && driverId.Value == localPlayerId.Value)
        {
            return true;
        }

        return false;
    }

    private bool? ReadNamedBool(object target, Type type, IEnumerable<string> nameFragments)
    {
        foreach (var field in type.GetFields(_instanceFlags))
        {
            if (field.FieldType != typeof(bool))
            {
                continue;
            }

            var lowered = field.Name.ToLowerInvariant();
            if (!ContainsAny(lowered, nameFragments))
            {
                continue;
            }

            try
            {
                return (bool)field.GetValue(target)!;
            }
            catch
            {
            }
        }

        foreach (var property in type.GetProperties(_instanceFlags))
        {
            if (property.PropertyType != typeof(bool) || !property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var lowered = property.Name.ToLowerInvariant();
            if (!ContainsAny(lowered, nameFragments))
            {
                continue;
            }

            try
            {
                return (bool)property.GetValue(target)!;
            }
            catch
            {
            }
        }

        return null;
    }

    private int? ReadNamedInt(object target, Type type, IEnumerable<string> nameFragments)
    {
        foreach (var field in type.GetFields(_instanceFlags))
        {
            if (field.FieldType != typeof(int))
            {
                continue;
            }

            var lowered = field.Name.ToLowerInvariant();
            if (!ContainsAny(lowered, nameFragments))
            {
                continue;
            }

            try
            {
                return (int)field.GetValue(target)!;
            }
            catch
            {
            }
        }

        foreach (var property in type.GetProperties(_instanceFlags))
        {
            if (property.PropertyType != typeof(int) || !property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var lowered = property.Name.ToLowerInvariant();
            if (!ContainsAny(lowered, nameFragments))
            {
                continue;
            }

            try
            {
                return (int)property.GetValue(target)!;
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool ContainsAny(string source, IEnumerable<string> fragments)
    {
        foreach (var fragment in fragments)
        {
            if (source.Contains(fragment))
            {
                return true;
            }
        }

        return false;
    }

    private bool? GetBoolMemberValue(object target, Type type, string memberName)
    {
        var value = GetMemberValueByName(target, type, memberName);
        return value is bool b ? b : null;
    }

    private long? GetNumberMemberValue(object target, Type type, string memberName)
    {
        var value = GetMemberValueByName(target, type, memberName);
        if (value == null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private object? GetMemberValueByName(object target, Type type, string memberName)
    {
        var field = type.GetField(memberName, _instanceFlags);
        if (field != null)
        {
            try
            {
                return field.GetValue(target);
            }
            catch
            {
            }
        }

        var property = type.GetProperty(memberName, _instanceFlags);
        if (property != null)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
            }
        }

        return null;
    }

    private object? GetSingletonInstance(string typeName)
    {
        var type = FindTypeByName(typeName);
        if (type == null)
        {
            return null;
        }

        var instanceField = type.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (instanceField != null)
        {
            try
            {
                return instanceField.GetValue(null);
            }
            catch
            {
            }
        }

        var instanceProperty = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (instanceProperty != null)
        {
            try
            {
                return instanceProperty.GetValue(null);
            }
            catch
            {
            }
        }

        return null;
    }

    private object? GetMemberValue(object? target, string memberName)
    {
        if (target == null)
        {
            return null;
        }

        var type = target.GetType();

        var field = type.GetField(memberName, _instanceFlags);
        if (field != null)
        {
            try
            {
                return field.GetValue(target);
            }
            catch
            {
            }
        }

        var property = type.GetProperty(memberName, _instanceFlags);
        if (property != null)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
            }
        }

        return null;
    }

    private static Type? FindTypeByName(string typeName)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            Type? exact;
            try
            {
                exact = assembly.GetType(typeName, false);
            }
            catch
            {
                continue;
            }

            if (exact != null)
            {
                return exact;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var candidate in types)
            {
                if (string.Equals(candidate.Name, typeName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private Transform? ResolveTransform(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is Transform transform)
        {
            return transform;
        }

        if (value is Component component)
        {
            return component.transform;
        }

        if (value is GameObject gameObject)
        {
            return gameObject.transform;
        }

        return null;
    }

    private static Transform? GetStableVehicleTransform(Transform? vehicleTransform)
    {
        if (vehicleTransform == null)
        {
            return null;
        }

        return vehicleTransform.root != null ? vehicleTransform.root : vehicleTransform;
    }

    private void ConfigureFeedImage(RawImage image, float posX, float posY, float width, float height)
    {
        image.raycastTarget = false;
        image.color = new Color(1f, 1f, 1f, Mathf.Clamp01(_feedOpacity?.Value ?? DefaultFeedOpacity));

        if (image.material == null)
        {
            image.material = new Material(Shader.Find("Unlit/Texture") ?? Shader.Find("UI/Default"));
        }

        var rect = image.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(posX, posY);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void ConfigureMirrorCamera(Camera? camera, RenderTexture? targetTexture, float fieldOfView)
    {
        if (camera == null)
        {
            return;
        }

        camera.fieldOfView = fieldOfView;
        camera.farClipPlane = Mathf.Clamp(RearCameraFarClip, 10f, 200f);
        camera.nearClipPlane = 0.1f;
        camera.targetTexture = targetTexture;
        camera.depth = -100f;
        camera.forceIntoRenderTexture = true;
        camera.rect = new Rect(0, 0, 1, 1);

        var mainCamera = Camera.main;
        camera.cullingMask = mainCamera != null ? mainCamera.cullingMask : ~0;
    }

    private sealed class DriverState
    {
        public bool IsInVehicle { get; set; }
        public bool IsDriver { get; set; }
        public Transform? VehicleTransform { get; set; }
    }
}
