using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

/// <summary>Runtime-safe manifest and validation helpers for a separately distributable city data package.</summary>
[Serializable]
public sealed class EnvironmentCostRuntimeCityPackageManifest
{
    public string schemaVersion;
    public string areaId;
    public string displayName;
    public string version;
    public string generatedAtUtc;
    public int coordinateZoneId;
    public double[] center;
    public double radiusMeters;
    public string inspectionSceneAssetPath;
    public EnvironmentCostRuntimeCityPackageScene scene;
    public EnvironmentCostRuntimeCityPackageSource[] sources;
    public EnvironmentCostRuntimeCityPackageFile[] files;

    public void ValidateStructure()
    {
        if (!string.Equals(schemaVersion, "environment-cost-runtime-city-package-0.1", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported runtime city package schema: {schemaVersion ?? "<null>"}.");
        if (string.IsNullOrWhiteSpace(areaId) || string.IsNullOrWhiteSpace(version) || coordinateZoneId < 1 || coordinateZoneId > 19 ||
            center == null || center.Length != 2 || radiusMeters <= 0.0 || string.IsNullOrWhiteSpace(inspectionSceneAssetPath))
            throw new InvalidOperationException("Runtime city package metadata is incomplete.");
        if (scene == null || scene.requiredLayers == null || scene.requiredLayers.Length == 0)
            throw new InvalidOperationException("Runtime city package does not describe the required scene layers.");
        if (files == null || files.Length == 0) throw new InvalidOperationException("Runtime city package contains no files.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.relativePath) || file.bytes < 0 || !IsSha256(file.sha256) ||
                !IsSafeRelativePath(file.relativePath) || !seen.Add(file.relativePath))
                throw new InvalidOperationException("Runtime city package file inventory is invalid.");
        }
    }

    public static bool IsSafeRelativePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
            path.IndexOf("..", StringComparison.Ordinal) < 0 && path.IndexOf(':') < 0;
    }

    public static string CalculateSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
        foreach (var character in value)
        {
            var digit = character >= '0' && character <= '9';
            var lower = character >= 'a' && character <= 'f';
            if (!digit && !lower) return false;
        }
        return true;
    }
}

[Serializable]
public sealed class EnvironmentCostRuntimeCityPackageScene
{
    public EnvironmentCostRuntimeCityPackageLayer[] requiredLayers;
}

[Serializable]
public sealed class EnvironmentCostRuntimeCityPackageLayer
{
    public string name;
    public int layer;
    public string role;
}

[Serializable]
public sealed class EnvironmentCostRuntimeCityPackageSource
{
    public string kind;
    public string originalPath;
    public string sha256;
}

[Serializable]
public sealed class EnvironmentCostRuntimeCityPackageFile
{
    public string kind;
    public string relativePath;
    public long bytes;
    public string sha256;
}
