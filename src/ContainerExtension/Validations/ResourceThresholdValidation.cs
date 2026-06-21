using System;
using System.Globalization;
using OneWare.Essentials.Models;

namespace ContainerExtension.Validations;

/// <summary>
/// Validates numeric resource thresholds (Memory/CPU) ensuring they are positive, finite, 
/// and within safe operational limits relative to host capacity.
/// </summary>
internal sealed class ResourceThresholdValidation : ISettingValidation
{
    private readonly double _threshold;
    private readonly double _total;
    private readonly string _resourceName;
    private readonly ResourceKind _resourceKind;

    public ResourceThresholdValidation(double threshold, double total, string resourceName)
    {
        _resourceName = resourceName ?? throw new ArgumentNullException(nameof(resourceName));
        _resourceKind = _resourceName.Equals("memory", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Memory :
                        _resourceName.Equals("CPU", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Cpu :
                        ResourceKind.Custom;

        if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total))
        {
            total = _resourceKind == ResourceKind.Memory ? 4096.0 :
                    _resourceKind == ResourceKind.Cpu ? 4.0 : 1.0;
        }
        if (threshold <= 0 || double.IsNaN(threshold) || double.IsInfinity(threshold))
        {
            threshold = total * 0.75;
        }

        _threshold = threshold;
        _total = total;
    }

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        double numericValue;

        if (value is string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return true;
            }
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                numericValue = parsed;
            }
            else
            {
                warningMessage = $"Value for {_resourceName} must be a valid number.";
                return false;
            }
        }
        else
        {
            switch (value)
            {
                case double d:
                    numericValue = d;
                    break;
                case float f:
                    numericValue = f;
                    break;
                case int i:
                    numericValue = i;
                    break;
                case long l:
                    numericValue = l;
                    break;
                case ulong ul:
                    numericValue = ul;
                    break;
                case decimal dec:
                    numericValue = (double)dec;
                    break;
                case uint ui:
                    numericValue = ui;
                    break;
                case ushort us:
                    numericValue = us;
                    break;
                case short sh:
                    numericValue = sh;
                    break;
                case byte b:
                    numericValue = b;
                    break;
                case sbyte sb:
                    numericValue = sb;
                    break;
                default:
                    return true;
            }
        }

        if (double.IsNaN(numericValue) || double.IsInfinity(numericValue))
        {
            warningMessage = $"Value for {_resourceName} must be a valid number.";
            return false;
        }

        if (numericValue < 0)
        {
            warningMessage = $"Value for {_resourceName} cannot be negative.";
            return false;
        }

        if (numericValue > 9_000_000_000_000_000.0)
        {
            warningMessage = $"Value for {_resourceName} exceeds safe integer boundaries.";
            return false;
        }

        if (numericValue == 0)
        {
            return true;
        }

        if (_resourceKind == ResourceKind.Memory && numericValue < 512.0)
        {
            warningMessage = "Memory limit must be at least 512 MB (or 0 for unlimited).";
            return false;
        }

        if (_resourceKind == ResourceKind.Cpu && (numericValue < 0.1 || numericValue > Math.Max(32.0, _total)))
        {
            var maxLimit = Math.Max(32.0, _total);
            warningMessage = string.Create(CultureInfo.InvariantCulture, $"CPU cores limit must be between 0.1 and {maxLimit.ToString("F1", CultureInfo.InvariantCulture)} (or 0 for unlimited).");
            return false;
        }

        if (numericValue > _total)
        {
            var format = _resourceKind == ResourceKind.Cpu ? "F1" : "N0";
            warningMessage = string.Create(CultureInfo.InvariantCulture, $"Value {numericValue.ToString(format, CultureInfo.InvariantCulture)} exceeds host {_resourceName} capacity ({_total.ToString(format, CultureInfo.InvariantCulture)}). Use a value between 0 and {_total.ToString(format, CultureInfo.InvariantCulture)}.");
            return false;
        }

        if (numericValue > _threshold)
        {
            warningMessage = string.Create(CultureInfo.InvariantCulture, $"Warning: Allocating {(numericValue / _total * 100):F0}% of host {_resourceName}. Values above 75% may starve the host OS.");
            return true;
        }

        return true;
    }
}
