// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using Aspire.Dashboard.Resources;

namespace Aspire.Dashboard.Model.Otlp;

public class FilterDialogFormModel : IValidatableObject
{
    [Required(ErrorMessageResourceType = typeof(Dialogs), ErrorMessageResourceName = nameof(Dialogs.FieldRequired))]
    public SelectViewModel<string>? Parameter { get; set; }

    [Required(ErrorMessageResourceType = typeof(Dialogs), ErrorMessageResourceName = nameof(Dialogs.FieldRequired))]
    public SelectViewModel<FilterCondition>? Condition { get; set; }

    public bool ValueIsNumeric { get; set; }

    public double? NumericValue { get; set; }

    // Set a max length on value because it will be added to the query string.
    // Max length is protection against accidently building a query string that exceeds limits because of a very long value.
    [MaxLength(1024, ErrorMessageResourceType = typeof(Dialogs), ErrorMessageResourceName = nameof(Dialogs.FieldTooLong))]
    public string? Value { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ValueIsNumeric)
        {
            if (NumericValue is not { } numericValue || !double.IsFinite(numericValue))
            {
                yield return new ValidationResult(Dialogs.FieldRequired, [nameof(NumericValue)]);
            }
        }
        else if (string.IsNullOrWhiteSpace(Value))
        {
            yield return new ValidationResult(Dialogs.FieldRequired, [nameof(Value)]);
        }
    }
}
