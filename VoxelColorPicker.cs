using Meta.XR;
using PassthroughCameraSamples;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VoxelColorPicker : MonoBehaviour {
    [SerializeField] private SamplingMode samplingMode = SamplingMode.Environment;

    [Header("Environment Sampling")]
    [SerializeField] private Transform[] raySampleOrigins;

    [Header("Brightness Correction")]
    [SerializeField, Range(0f, 1f)] private float targetBrightness = 0.8f;
    [SerializeField, Range(0f, 1f)] private float correctionSmoothing = 0.5f;
    [SerializeField] private int roiSize = 3;
    [SerializeField] private float minCorrection = 0.8f;
    [SerializeField] private float maxCorrection = 1.5f;

    [Header("Voxel Extension")]
    [SerializeField] private GameObject cubePrefab;
    [SerializeField] private float voxelSize = 0.25f;

    private HashSet<Vector3Int> occupiedVoxels = new HashSet<Vector3Int>();

    // Offset in meters to pull the point slightly away from the surface normal
    private float surfaceOffset = 0.05f;

    private float _prevCorrectionFactor = 1f;
    private Vector3? _lastHitPoint;
    private Camera _mainCamera;
    private Renderer _manualRenderer;
    private WebCamTexture _webcamTexture;
    private WebCamTextureManager _cameraManager;
    private EnvironmentRaycastManager _raycastManager;

    private bool isScanning = true;

    private void Start() {
        _mainCamera = Camera.main;
        _cameraManager = FindAnyObjectByType<WebCamTextureManager>();
        _raycastManager = GetComponent<EnvironmentRaycastManager>();

        if (!_mainCamera || !_cameraManager || !_raycastManager ||
            (samplingMode == SamplingMode.Environment && raySampleOrigins.Length <= 0) ||
            (samplingMode == SamplingMode.Manual)) {
            Debug.LogError("ColorPicker: Missing required references.");
            return;
        }

        surfaceOffset = voxelSize / 2f;

        StartCoroutine(WaitForWebCam());
    }

    private IEnumerator WaitForWebCam() {
        while (!_cameraManager.WebCamTexture || !_cameraManager.WebCamTexture.isPlaying) {
            yield return null;
        }

        _webcamTexture = _cameraManager.WebCamTexture;
    }

    private void Update() {
        if (isScanning) {
            foreach (var raySampleOrigin in raySampleOrigins) {
                UpdateSamplingPoint(raySampleOrigin);
            }
        }
    }

    private void UpdateSamplingPoint(Transform raySampleOrigin) {
        if (samplingMode == SamplingMode.Environment) {
            Ray ray = new(raySampleOrigin.position, raySampleOrigin.forward);
            var hitSuccess = _raycastManager.Raycast(ray, out var hit);

            _lastHitPoint = hitSuccess ? hit.point : null;

            if (hitSuccess && hit.status == EnvironmentRaycastHitStatus.Hit) {
                // Offset the point slightly out of the surface to avoid snapping inside
                Vector3 adjustedPoint = hit.point + hit.normal * surfaceOffset;
                Vector3 voxelPosition = SnapToVoxel(hit.point);

                Vector3Int voxelKey = WorldToVoxelKey(voxelPosition);
                if (!occupiedVoxels.Contains(voxelKey)) {
                    var voxelCube = Instantiate(cubePrefab, voxelPosition, Quaternion.identity);
                    voxelCube.transform.localScale = new Vector3(voxelSize, voxelSize, voxelSize);
                    occupiedVoxels.Add(voxelKey);

                    // pick color for last placed cube
                    _manualRenderer = voxelCube.GetComponent<Renderer>();
                    PickColor();
                }
            }
        }
    }

    private void PickColor() {
        if (_lastHitPoint == null || !_webcamTexture || !_webcamTexture.isPlaying) {
            Debug.LogWarning("ColorPicker: Invalid sampling point or webcam texture not ready.");
            return;
        }

        var uv = WorldToTextureUV(_lastHitPoint.Value);
        var color = SampleAndCorrectColor(uv);

        if (_manualRenderer) {
            _manualRenderer.material.color = color;
        }
    }

    private Vector2 WorldToTextureUV(Vector3 worldPoint) {
        var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(_cameraManager.Eye);
        var localPoint = Quaternion.Inverse(cameraPose.rotation) * (worldPoint - cameraPose.position);
        var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(_cameraManager.Eye);

        if (localPoint.z <= 0.0001f) {
            Debug.LogWarning("ColorPicker: Point too close.");
            return Vector2.zero;
        }

        var scaleX = _webcamTexture.width / (float)intrinsics.Resolution.x;
        var scaleY = _webcamTexture.height / (float)intrinsics.Resolution.y;

        var uPixel = intrinsics.FocalLength.x * (localPoint.x / localPoint.z) + intrinsics.PrincipalPoint.x;
        var vPixel = intrinsics.FocalLength.y * (localPoint.y / localPoint.z) + intrinsics.PrincipalPoint.y;

        uPixel *= scaleX;
        vPixel *= scaleY;

        var u = uPixel / _webcamTexture.width;
        var v = vPixel / _webcamTexture.height;

        return new Vector2(u, v);
    }

    private Color SampleAndCorrectColor(Vector2 uv) {
        var x = Mathf.Clamp(Mathf.RoundToInt(uv.x * _webcamTexture.width), 0, _webcamTexture.width - 1);
        var y = Mathf.Clamp(Mathf.RoundToInt(uv.y * _webcamTexture.height), 0, _webcamTexture.height - 1);

        var sampledColor = _webcamTexture.GetPixel(x, y);

        var brightness = CalculateRoiBrightness(x, y);

        var factor = Mathf.Clamp(targetBrightness / Mathf.Max(brightness, 0.001f), minCorrection, maxCorrection);
        _prevCorrectionFactor = Mathf.Lerp(_prevCorrectionFactor, factor, correctionSmoothing);

        var corrected = (sampledColor.linear * _prevCorrectionFactor).gamma;
        return new Color(Mathf.Clamp01(corrected.r), Mathf.Clamp01(corrected.g), Mathf.Clamp01(corrected.b), corrected.a);
    }

    private float CalculateRoiBrightness(int x, int y) {
        var sum = 0f;
        var count = 0;
        var half = roiSize / 2;

        for (var i = -half; i <= half; i++) {
            for (var j = -half; j <= half; j++) {
                int xi = x + i, yj = y + j;
                if (xi < 0 || xi >= _webcamTexture.width || yj < 0 || yj >= _webcamTexture.height) {
                    continue;
                }

                var pixel = _webcamTexture.GetPixel(xi, yj).linear;
                sum += 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
                count++;
            }
        }

        return count > 0 ? sum / count : 0f;
    }

    private Vector3 SnapToVoxel(Vector3 position) {
        float x = Mathf.Floor(position.x / voxelSize) * voxelSize + voxelSize / 2f;
        float y = Mathf.Floor(position.y / voxelSize) * voxelSize + voxelSize / 2f;
        float z = Mathf.Floor(position.z / voxelSize) * voxelSize + voxelSize / 2f;
        return new Vector3(x, y, z);
    }

    private Vector3Int WorldToVoxelKey(Vector3 worldPos) {
        int x = Mathf.FloorToInt(worldPos.x / voxelSize);
        int y = Mathf.FloorToInt(worldPos.y / voxelSize);
        int z = Mathf.FloorToInt(worldPos.z / voxelSize);
        return new Vector3Int(x, y, z);
    }
}