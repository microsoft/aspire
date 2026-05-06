// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;

namespace Aspire.Hosting.Foundry;

internal sealed class FoundryAnthropicDeploymentModel : CognitiveServicesAccountDeploymentModel
{
    private BicepValue<string>? _countryCode;
    private BicepValue<string>? _industry;
    private BicepValue<string>? _organizationName;

    public BicepValue<string> CountryCode
    {
        get
        {
            Initialize();
            return _countryCode!;
        }
        set
        {
            Initialize();
            _countryCode!.Assign(value);
        }
    }

    public BicepValue<string> Industry
    {
        get
        {
            Initialize();
            return _industry!;
        }
        set
        {
            Initialize();
            _industry!.Assign(value);
        }
    }

    public BicepValue<string> OrganizationName
    {
        get
        {
            Initialize();
            return _organizationName!;
        }
        set
        {
            Initialize();
            _organizationName!.Assign(value);
        }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();

        _industry = DefineProperty<string>(nameof(Industry), ["modelProviderData", "industry"]);
        _organizationName = DefineProperty<string>(nameof(OrganizationName), ["modelProviderData", "organizationName"]);
        _countryCode = DefineProperty<string>(nameof(CountryCode), ["modelProviderData", "countryCode"]);
    }
}
