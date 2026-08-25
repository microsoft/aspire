// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Azure.ApiManagement.Provisioning;

internal sealed class ApiManagementServiceProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<AzureLocation>? _location;
    private BicepValue<string>? _publisherEmail;
    private BicepValue<string>? _publisherName;
    private BicepValue<string>? _skuName;
    private BicepValue<int>? _skuCapacity;
    private ManagedServiceIdentity? _identity;
    private BicepValue<string>? _publicNetworkAccess;
    private BicepValue<string>? _virtualNetworkType;
    private BicepValue<string>? _subnetResourceId;
    private BicepList<ApiManagementHostnameConfigurationProvisioningModel>? _hostnameConfigurations;
    private BicepDictionary<string>? _tags;
    private BicepValue<Uri>? _gatewayUri;
    private BicepValue<ResourceIdentifier>? _id;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> PublisherEmail
    {
        get { Initialize(); return _publisherEmail!; }
        set { Initialize(); _publisherEmail!.Assign(value); }
    }

    public BicepValue<string> PublisherName
    {
        get { Initialize(); return _publisherName!; }
        set { Initialize(); _publisherName!.Assign(value); }
    }

    public BicepValue<string> SkuName
    {
        get { Initialize(); return _skuName!; }
        set { Initialize(); _skuName!.Assign(value); }
    }

    public BicepValue<int> SkuCapacity
    {
        get { Initialize(); return _skuCapacity!; }
        set { Initialize(); _skuCapacity!.Assign(value); }
    }

    public ManagedServiceIdentity Identity
    {
        get { Initialize(); return _identity!; }
        set { Initialize(); AssignOrReplace(ref _identity, value); }
    }

    public BicepValue<string> PublicNetworkAccess
    {
        get { Initialize(); return _publicNetworkAccess!; }
        set { Initialize(); _publicNetworkAccess!.Assign(value); }
    }

    public BicepValue<string> VirtualNetworkType
    {
        get { Initialize(); return _virtualNetworkType!; }
        set { Initialize(); _virtualNetworkType!.Assign(value); }
    }

    public BicepValue<string> SubnetResourceId
    {
        get { Initialize(); return _subnetResourceId!; }
        set { Initialize(); _subnetResourceId!.Assign(value); }
    }

    public BicepList<ApiManagementHostnameConfigurationProvisioningModel> HostnameConfigurations
    {
        get { Initialize(); return _hostnameConfigurations!; }
        set { Initialize(); _hostnameConfigurations!.Assign(value); }
    }

    public BicepDictionary<string> Tags
    {
        get { Initialize(); return _tags!; }
        set { Initialize(); _tags!.Assign(value); }
    }

    public BicepValue<Uri> GatewayUri
    {
        get { Initialize(); return _gatewayUri!; }
    }

    public BicepValue<ResourceIdentifier> Id
    {
        get { Initialize(); return _id!; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _location = DefineProperty<AzureLocation>("Location", ["location"], isRequired: true);
        _publisherEmail = DefineProperty<string>(nameof(PublisherEmail), ["properties", "publisherEmail"], isRequired: true);
        _publisherName = DefineProperty<string>(nameof(PublisherName), ["properties", "publisherName"], isRequired: true);
        _skuName = DefineProperty<string>(nameof(SkuName), ["sku", "name"], isRequired: true);
        _skuCapacity = DefineProperty<int>(nameof(SkuCapacity), ["sku", "capacity"], isRequired: true);
        _identity = DefineModelProperty<ManagedServiceIdentity>(nameof(Identity), ["identity"]);
        _publicNetworkAccess = DefineProperty<string>(nameof(PublicNetworkAccess), ["properties", "publicNetworkAccess"]);
        _virtualNetworkType = DefineProperty<string>(nameof(VirtualNetworkType), ["properties", "virtualNetworkType"]);
        _subnetResourceId = DefineProperty<string>(nameof(SubnetResourceId), ["properties", "virtualNetworkConfiguration", "subnetResourceId"]);
        _hostnameConfigurations = DefineListProperty<ApiManagementHostnameConfigurationProvisioningModel>(
            nameof(HostnameConfigurations), ["properties", "hostnameConfigurations"]);
        _tags = DefineDictionaryProperty<string>(nameof(Tags), ["tags"]);
        _gatewayUri = DefineProperty<Uri>(nameof(GatewayUri), ["properties", "gatewayUrl"], isOutput: true);
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
    }
}

internal sealed class ApiManagementHostnameConfigurationProvisioningModel : ProvisionableConstruct
{
    private BicepValue<string>? _type;
    private BicepValue<string>? _hostName;
    private BicepValue<string>? _keyVaultId;
    private BicepValue<string>? _identityClientId;
    private BicepValue<bool>? _defaultSslBinding;
    private BicepValue<bool>? _negotiateClientCertificate;

    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }

    public BicepValue<string> HostName
    {
        get { Initialize(); return _hostName!; }
        set { Initialize(); _hostName!.Assign(value); }
    }

    public BicepValue<string> KeyVaultId
    {
        get { Initialize(); return _keyVaultId!; }
        set { Initialize(); _keyVaultId!.Assign(value); }
    }

    public BicepValue<string> IdentityClientId
    {
        get { Initialize(); return _identityClientId!; }
        set { Initialize(); _identityClientId!.Assign(value); }
    }

    public BicepValue<bool> DefaultSslBinding
    {
        get { Initialize(); return _defaultSslBinding!; }
        set { Initialize(); _defaultSslBinding!.Assign(value); }
    }

    public BicepValue<bool> NegotiateClientCertificate
    {
        get { Initialize(); return _negotiateClientCertificate!; }
        set { Initialize(); _negotiateClientCertificate!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _type = DefineProperty<string>(nameof(Type), ["type"], isRequired: true);
        _hostName = DefineProperty<string>(nameof(HostName), ["hostName"], isRequired: true);
        _keyVaultId = DefineProperty<string>(nameof(KeyVaultId), ["keyVaultId"], isRequired: true);
        _identityClientId = DefineProperty<string>(nameof(IdentityClientId), ["identityClientId"]);
        _defaultSslBinding = DefineProperty<bool>(nameof(DefaultSslBinding), ["defaultSslBinding"]);
        _negotiateClientCertificate = DefineProperty<bool>(nameof(NegotiateClientCertificate), ["negotiateClientCertificate"]);
    }
}

internal sealed class ApiManagementBackendProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/backends", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _protocol;
    private BicepValue<string>? _uri;
    private BicepValue<string>? _title;
    private BicepValue<string>? _type;
    private BicepValue<bool>? _validateCertificateChain;
    private BicepValue<bool>? _validateCertificateName;
    private ApiManagementBackendPoolProvisioningModel? _pool;
    private ApiManagementCircuitBreakerProvisioningModel? _circuitBreaker;
    private BicepValue<ResourceIdentifier>? _id;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> Protocol
    {
        get { Initialize(); return _protocol!; }
        set { Initialize(); _protocol!.Assign(value); }
    }

    public BicepValue<string> Uri
    {
        get { Initialize(); return _uri!; }
        set { Initialize(); _uri!.Assign(value); }
    }

    public BicepValue<string> Title
    {
        get { Initialize(); return _title!; }
        set { Initialize(); _title!.Assign(value); }
    }

    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }

    public BicepValue<bool> ValidateCertificateChain
    {
        get { Initialize(); return _validateCertificateChain!; }
        set { Initialize(); _validateCertificateChain!.Assign(value); }
    }

    public BicepValue<bool> ValidateCertificateName
    {
        get { Initialize(); return _validateCertificateName!; }
        set { Initialize(); _validateCertificateName!.Assign(value); }
    }

    public ApiManagementBackendPoolProvisioningModel Pool
    {
        get { Initialize(); return _pool!; }
        set { Initialize(); AssignOrReplace(ref _pool, value); }
    }

    public ApiManagementCircuitBreakerProvisioningModel CircuitBreaker
    {
        get { Initialize(); return _circuitBreaker!; }
        set { Initialize(); AssignOrReplace(ref _circuitBreaker, value); }
    }

    public BicepValue<ResourceIdentifier> Id
    {
        get { Initialize(); return _id!; }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        // Pool backends omit protocol and URL; both are required only for Single backends.
        _protocol = DefineProperty<string>(nameof(Protocol), ["properties", "protocol"]);
        _uri = DefineProperty<string>(nameof(Uri), ["properties", "url"]);
        _title = DefineProperty<string>(nameof(Title), ["properties", "title"]);
        _type = DefineProperty<string>(nameof(Type), ["properties", "type"]);
        _validateCertificateChain = DefineProperty<bool>(nameof(ValidateCertificateChain), ["properties", "tls", "validateCertificateChain"]);
        _validateCertificateName = DefineProperty<bool>(nameof(ValidateCertificateName), ["properties", "tls", "validateCertificateName"]);
        _pool = DefineModelProperty<ApiManagementBackendPoolProvisioningModel>(nameof(Pool), ["properties", "pool"]);
        _circuitBreaker = DefineModelProperty<ApiManagementCircuitBreakerProvisioningModel>(nameof(CircuitBreaker), ["properties", "circuitBreaker"]);
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementBackendPoolProvisioningModel : ProvisionableConstruct
{
    private BicepList<ApiManagementBackendPoolMemberProvisioningModel>? _services;

    public BicepList<ApiManagementBackendPoolMemberProvisioningModel> Services
    {
        get { Initialize(); return _services!; }
        set { Initialize(); _services!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _services = DefineListProperty<ApiManagementBackendPoolMemberProvisioningModel>(nameof(Services), ["services"], isRequired: true);
    }
}

internal sealed class ApiManagementBackendPoolMemberProvisioningModel : ProvisionableConstruct
{
    private BicepValue<ResourceIdentifier>? _id;
    private BicepValue<int>? _priority;
    private BicepValue<int>? _weight;

    public BicepValue<ResourceIdentifier> Id
    {
        get { Initialize(); return _id!; }
        set { Initialize(); _id!.Assign(value); }
    }

    public BicepValue<int> Priority
    {
        get { Initialize(); return _priority!; }
        set { Initialize(); _priority!.Assign(value); }
    }

    public BicepValue<int> Weight
    {
        get { Initialize(); return _weight!; }
        set { Initialize(); _weight!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isRequired: true);
        _priority = DefineProperty<int>(nameof(Priority), ["priority"]);
        _weight = DefineProperty<int>(nameof(Weight), ["weight"]);
    }
}

internal sealed class ApiManagementCircuitBreakerProvisioningModel : ProvisionableConstruct
{
    private BicepList<ApiManagementCircuitBreakerRuleProvisioningModel>? _rules;

    public BicepList<ApiManagementCircuitBreakerRuleProvisioningModel> Rules
    {
        get { Initialize(); return _rules!; }
        set { Initialize(); _rules!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _rules = DefineListProperty<ApiManagementCircuitBreakerRuleProvisioningModel>(nameof(Rules), ["rules"], isRequired: true);
    }
}

internal sealed class ApiManagementCircuitBreakerRuleProvisioningModel : ProvisionableConstruct
{
    private BicepValue<string>? _name;
    private ApiManagementCircuitBreakerFailureConditionProvisioningModel? _failureCondition;
    private BicepValue<string>? _tripDuration;
    private BicepValue<bool>? _acceptRetryAfter;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public ApiManagementCircuitBreakerFailureConditionProvisioningModel FailureCondition
    {
        get { Initialize(); return _failureCondition!; }
        set { Initialize(); AssignOrReplace(ref _failureCondition, value); }
    }

    public BicepValue<string> TripDuration
    {
        get { Initialize(); return _tripDuration!; }
        set { Initialize(); _tripDuration!.Assign(value); }
    }

    public BicepValue<bool> AcceptRetryAfter
    {
        get { Initialize(); return _acceptRetryAfter!; }
        set { Initialize(); _acceptRetryAfter!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _failureCondition = DefineModelProperty<ApiManagementCircuitBreakerFailureConditionProvisioningModel>(
            nameof(FailureCondition), ["failureCondition"], isRequired: true);
        _tripDuration = DefineProperty<string>(nameof(TripDuration), ["tripDuration"], isRequired: true);
        _acceptRetryAfter = DefineProperty<bool>(nameof(AcceptRetryAfter), ["acceptRetryAfter"]);
    }
}

internal sealed class ApiManagementCircuitBreakerFailureConditionProvisioningModel : ProvisionableConstruct
{
    private BicepValue<int>? _count;
    private BicepValue<string>? _interval;
    private BicepList<ApiManagementStatusCodeRangeProvisioningModel>? _statusCodeRanges;

    public BicepValue<int> Count
    {
        get { Initialize(); return _count!; }
        set { Initialize(); _count!.Assign(value); }
    }

    public BicepValue<string> Interval
    {
        get { Initialize(); return _interval!; }
        set { Initialize(); _interval!.Assign(value); }
    }

    public BicepList<ApiManagementStatusCodeRangeProvisioningModel> StatusCodeRanges
    {
        get { Initialize(); return _statusCodeRanges!; }
        set { Initialize(); _statusCodeRanges!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _count = DefineProperty<int>(nameof(Count), ["count"], isRequired: true);
        _interval = DefineProperty<string>(nameof(Interval), ["interval"], isRequired: true);
        _statusCodeRanges = DefineListProperty<ApiManagementStatusCodeRangeProvisioningModel>(
            nameof(StatusCodeRanges), ["statusCodeRanges"], isRequired: true);
    }
}

internal sealed class ApiManagementStatusCodeRangeProvisioningModel : ProvisionableConstruct
{
    private BicepValue<int>? _minimum;
    private BicepValue<int>? _maximum;

    public BicepValue<int> Minimum
    {
        get { Initialize(); return _minimum!; }
        set { Initialize(); _minimum!.Assign(value); }
    }

    public BicepValue<int> Maximum
    {
        get { Initialize(); return _maximum!; }
        set { Initialize(); _maximum!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _minimum = DefineProperty<int>(nameof(Minimum), ["min"], isRequired: true);
        _maximum = DefineProperty<int>(nameof(Maximum), ["max"], isRequired: true);
    }
}

internal sealed class ApiManagementApiProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/apis", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _displayName;
    private BicepValue<string>? _path;
    private BicepValue<bool>? _subscriptionRequired;
    private BicepValue<string>? _type;
    private BicepList<string>? _protocols;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }

    public BicepValue<string> Path
    {
        get { Initialize(); return _path!; }
        set { Initialize(); _path!.Assign(value); }
    }

    public BicepValue<bool> SubscriptionRequired
    {
        get { Initialize(); return _subscriptionRequired!; }
        set { Initialize(); _subscriptionRequired!.Assign(value); }
    }

    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }

    public BicepList<string> Protocols
    {
        get { Initialize(); return _protocols!; }
        set { Initialize(); _protocols!.Assign(value); }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["properties", "displayName"], isRequired: true);
        _path = DefineProperty<string>(nameof(Path), ["properties", "path"], isRequired: true);
        _subscriptionRequired = DefineProperty<bool>(nameof(SubscriptionRequired), ["properties", "subscriptionRequired"]);
        _type = DefineProperty<string>(nameof(Type), ["properties", "type"]);
        _protocols = DefineListProperty<string>(nameof(Protocols), ["properties", "protocols"]);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementOperationProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/apis/operations", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _displayName;
    private BicepValue<string>? _method;
    private BicepValue<string>? _uriTemplate;
    private BicepList<ApiManagementParameterProvisioningModel>? _templateParameters;
    private ResourceReference<ApiManagementApiProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }

    public BicepValue<string> Method
    {
        get { Initialize(); return _method!; }
        set { Initialize(); _method!.Assign(value); }
    }

    public BicepValue<string> UriTemplate
    {
        get { Initialize(); return _uriTemplate!; }
        set { Initialize(); _uriTemplate!.Assign(value); }
    }

    public BicepList<ApiManagementParameterProvisioningModel> TemplateParameters
    {
        get { Initialize(); return _templateParameters!; }
        set { Initialize(); _templateParameters!.Assign(value); }
    }

    public ApiManagementApiProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["properties", "displayName"], isRequired: true);
        _method = DefineProperty<string>(nameof(Method), ["properties", "method"], isRequired: true);
        _uriTemplate = DefineProperty<string>(nameof(UriTemplate), ["properties", "urlTemplate"], isRequired: true);
        _templateParameters = DefineListProperty<ApiManagementParameterProvisioningModel>(nameof(TemplateParameters), ["properties", "templateParameters"]);
        _parent = DefineResource<ApiManagementApiProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementParameterProvisioningModel : ProvisionableConstruct
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _type;
    private BicepValue<bool>? _required;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }

    public BicepValue<bool> Required
    {
        get { Initialize(); return _required!; }
        set { Initialize(); _required!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _type = DefineProperty<string>(nameof(Type), ["type"], isRequired: true);
        _required = DefineProperty<bool>(nameof(Required), ["required"]);
    }
}

internal sealed class ApiManagementServicePolicyProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/policies", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _format;
    private BicepValue<string>? _value;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> Format
    {
        get { Initialize(); return _format!; }
        set { Initialize(); _format!.Assign(value); }
    }

    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _format = DefineProperty<string>(nameof(Format), ["properties", "format"]);
        _value = DefineProperty<string>(nameof(Value), ["properties", "value"]);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementApiPolicyProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/apis/policies", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _format;
    private BicepValue<string>? _value;
    private ResourceReference<ApiManagementApiProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> Format
    {
        get { Initialize(); return _format!; }
        set { Initialize(); _format!.Assign(value); }
    }

    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    public ApiManagementApiProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _format = DefineProperty<string>(nameof(Format), ["properties", "format"]);
        _value = DefineProperty<string>(nameof(Value), ["properties", "value"]);
        _parent = DefineResource<ApiManagementApiProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementOperationPolicyProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/apis/operations/policies", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _format;
    private BicepValue<string>? _value;
    private ResourceReference<ApiManagementOperationProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> Format
    {
        get { Initialize(); return _format!; }
        set { Initialize(); _format!.Assign(value); }
    }

    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    public ApiManagementOperationProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _format = DefineProperty<string>(nameof(Format), ["properties", "format"]);
        _value = DefineProperty<string>(nameof(Value), ["properties", "value"]);
        _parent = DefineResource<ApiManagementOperationProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementProductProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/products", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _displayName;
    private BicepValue<string>? _description;
    private BicepValue<string>? _terms;
    private BicepValue<bool>? _subscriptionRequired;
    private BicepValue<bool>? _approvalRequired;
    private BicepValue<int>? _subscriptionsLimit;
    private BicepValue<string>? _state;
    private BicepValue<ResourceIdentifier>? _id;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }

    public BicepValue<string> Description
    {
        get { Initialize(); return _description!; }
        set { Initialize(); _description!.Assign(value); }
    }

    public BicepValue<string> Terms
    {
        get { Initialize(); return _terms!; }
        set { Initialize(); _terms!.Assign(value); }
    }

    public BicepValue<bool> SubscriptionRequired
    {
        get { Initialize(); return _subscriptionRequired!; }
        set { Initialize(); _subscriptionRequired!.Assign(value); }
    }

    public BicepValue<bool> ApprovalRequired
    {
        get { Initialize(); return _approvalRequired!; }
        set { Initialize(); _approvalRequired!.Assign(value); }
    }

    public BicepValue<int> SubscriptionsLimit
    {
        get { Initialize(); return _subscriptionsLimit!; }
        set { Initialize(); _subscriptionsLimit!.Assign(value); }
    }

    public BicepValue<string> State
    {
        get { Initialize(); return _state!; }
        set { Initialize(); _state!.Assign(value); }
    }

    public BicepValue<ResourceIdentifier> Id
    {
        get { Initialize(); return _id!; }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["properties", "displayName"], isRequired: true);
        _description = DefineProperty<string>(nameof(Description), ["properties", "description"]);
        _terms = DefineProperty<string>(nameof(Terms), ["properties", "terms"]);
        _subscriptionRequired = DefineProperty<bool>(nameof(SubscriptionRequired), ["properties", "subscriptionRequired"]);
        _approvalRequired = DefineProperty<bool>(nameof(ApprovalRequired), ["properties", "approvalRequired"]);
        _subscriptionsLimit = DefineProperty<int>(nameof(SubscriptionsLimit), ["properties", "subscriptionsLimit"]);
        _state = DefineProperty<string>(nameof(State), ["properties", "state"]);
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementProductApiProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/products/apis", "2024-05-01")
{
    private BicepValue<string>? _name;
    private ResourceReference<ApiManagementProductProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public ApiManagementProductProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _parent = DefineResource<ApiManagementProductProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementSubscriptionProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/subscriptions", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _displayName;
    private BicepValue<ResourceIdentifier>? _scope;
    private BicepValue<string>? _state;
    private BicepValue<bool>? _allowTracing;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }

    public BicepValue<ResourceIdentifier> Scope
    {
        get { Initialize(); return _scope!; }
        set { Initialize(); _scope!.Assign(value); }
    }

    public BicepValue<string> State
    {
        get { Initialize(); return _state!; }
        set { Initialize(); _state!.Assign(value); }
    }

    public BicepValue<bool> AllowTracing
    {
        get { Initialize(); return _allowTracing!; }
        set { Initialize(); _allowTracing!.Assign(value); }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["properties", "displayName"], isRequired: true);
        _scope = DefineProperty<ResourceIdentifier>(nameof(Scope), ["properties", "scope"], isRequired: true);
        _state = DefineProperty<string>(nameof(State), ["properties", "state"]);
        _allowTracing = DefineProperty<bool>(nameof(AllowTracing), ["properties", "allowTracing"]);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementNamedValueProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/namedValues", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _displayName;
    private BicepValue<string>? _value;
    private BicepValue<bool>? _secret;
    private BicepList<string>? _tags;
    private ApiManagementKeyVaultNamedValueProvisioningModel? _keyVault;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }

    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    public BicepValue<bool> Secret
    {
        get { Initialize(); return _secret!; }
        set { Initialize(); _secret!.Assign(value); }
    }

    public BicepList<string> Tags
    {
        get { Initialize(); return _tags!; }
        set { Initialize(); _tags!.Assign(value); }
    }

    public ApiManagementKeyVaultNamedValueProvisioningModel KeyVault
    {
        get { Initialize(); return _keyVault!; }
        set { Initialize(); AssignOrReplace(ref _keyVault, value); }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["properties", "displayName"], isRequired: true);
        _value = DefineProperty<string>(nameof(Value), ["properties", "value"]);
        _secret = DefineProperty<bool>(nameof(Secret), ["properties", "secret"]);
        _tags = DefineListProperty<string>(nameof(Tags), ["properties", "tags"]);
        _keyVault = DefineModelProperty<ApiManagementKeyVaultNamedValueProvisioningModel>(nameof(KeyVault), ["properties", "keyVault"]);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementKeyVaultNamedValueProvisioningModel : ProvisionableConstruct
{
    private BicepValue<string>? _secretIdentifier;
    private BicepValue<string>? _identityClientId;

    public BicepValue<string> SecretIdentifier
    {
        get { Initialize(); return _secretIdentifier!; }
        set { Initialize(); _secretIdentifier!.Assign(value); }
    }

    public BicepValue<string> IdentityClientId
    {
        get { Initialize(); return _identityClientId!; }
        set { Initialize(); _identityClientId!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _secretIdentifier = DefineProperty<string>(nameof(SecretIdentifier), ["secretIdentifier"], isRequired: true);
        _identityClientId = DefineProperty<string>(nameof(IdentityClientId), ["identityClientId"]);
    }
}

internal sealed class ApiManagementPolicyFragmentProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/policyFragments", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _format;
    private BicepValue<string>? _value;
    private BicepValue<string>? _description;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> Format
    {
        get { Initialize(); return _format!; }
        set { Initialize(); _format!.Assign(value); }
    }

    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    public BicepValue<string> Description
    {
        get { Initialize(); return _description!; }
        set { Initialize(); _description!.Assign(value); }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _format = DefineProperty<string>(nameof(Format), ["properties", "format"]);
        _value = DefineProperty<string>(nameof(Value), ["properties", "value"], isRequired: true);
        _description = DefineProperty<string>(nameof(Description), ["properties", "description"]);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementLoggerProvisioningResource(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, "Microsoft.ApiManagement/service/loggers", "2024-05-01")
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _loggerType;
    private BicepValue<ResourceIdentifier>? _resourceId;
    private BicepDictionary<string>? _credentials;
    private BicepValue<bool>? _isBuffered;
    private BicepValue<ResourceIdentifier>? _id;
    private ResourceReference<ApiManagementServiceProvisioningResource>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<string> LoggerType
    {
        get { Initialize(); return _loggerType!; }
        set { Initialize(); _loggerType!.Assign(value); }
    }

    public BicepValue<ResourceIdentifier> ResourceId
    {
        get { Initialize(); return _resourceId!; }
        set { Initialize(); _resourceId!.Assign(value); }
    }

    public BicepDictionary<string> Credentials
    {
        get { Initialize(); return _credentials!; }
        set { Initialize(); _credentials!.Assign(value); }
    }

    public BicepValue<bool> IsBuffered
    {
        get { Initialize(); return _isBuffered!; }
        set { Initialize(); _isBuffered!.Assign(value); }
    }

    public BicepValue<ResourceIdentifier> Id
    {
        get { Initialize(); return _id!; }
    }

    public ApiManagementServiceProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _loggerType = DefineProperty<string>(nameof(LoggerType), ["properties", "loggerType"], isRequired: true);
        _resourceId = DefineProperty<ResourceIdentifier>(nameof(ResourceId), ["properties", "resourceId"]);
        _credentials = DefineDictionaryProperty<string>(nameof(Credentials), ["properties", "credentials"]);
        _isBuffered = DefineProperty<bool>(nameof(IsBuffered), ["properties", "isBuffered"]);
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
        _parent = DefineResource<ApiManagementServiceProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal abstract class ApiManagementDiagnosticProvisioningResource<TParent>(string bicepIdentifier, string resourceType)
    : ProvisionableResource(bicepIdentifier, resourceType, "2024-05-01")
    where TParent : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<ResourceIdentifier>? _loggerId;
    private BicepValue<string>? _alwaysLog;
    private ApiManagementSamplingProvisioningModel? _sampling;
    private BicepValue<string>? _httpCorrelationProtocol;
    private BicepValue<bool>? _logClientIp;
    private BicepValue<string>? _verbosity;
    private BicepValue<string>? _operationNameFormat;
    private BicepValue<bool>? _metrics;
    private ResourceReference<TParent>? _parent;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    public BicepValue<ResourceIdentifier> LoggerId
    {
        get { Initialize(); return _loggerId!; }
        set { Initialize(); _loggerId!.Assign(value); }
    }

    public BicepValue<string> AlwaysLog
    {
        get { Initialize(); return _alwaysLog!; }
        set { Initialize(); _alwaysLog!.Assign(value); }
    }

    public ApiManagementSamplingProvisioningModel Sampling
    {
        get { Initialize(); return _sampling!; }
        set { Initialize(); AssignOrReplace(ref _sampling, value); }
    }

    public BicepValue<string> HttpCorrelationProtocol
    {
        get { Initialize(); return _httpCorrelationProtocol!; }
        set { Initialize(); _httpCorrelationProtocol!.Assign(value); }
    }

    public BicepValue<bool> LogClientIp
    {
        get { Initialize(); return _logClientIp!; }
        set { Initialize(); _logClientIp!.Assign(value); }
    }

    public BicepValue<string> Verbosity
    {
        get { Initialize(); return _verbosity!; }
        set { Initialize(); _verbosity!.Assign(value); }
    }

    public BicepValue<string> OperationNameFormat
    {
        get { Initialize(); return _operationNameFormat!; }
        set { Initialize(); _operationNameFormat!.Assign(value); }
    }

    public BicepValue<bool> Metrics
    {
        get { Initialize(); return _metrics!; }
        set { Initialize(); _metrics!.Assign(value); }
    }

    public TParent? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _loggerId = DefineProperty<ResourceIdentifier>(nameof(LoggerId), ["properties", "loggerId"], isRequired: true);
        _alwaysLog = DefineProperty<string>(nameof(AlwaysLog), ["properties", "alwaysLog"]);
        _sampling = DefineModelProperty<ApiManagementSamplingProvisioningModel>(nameof(Sampling), ["properties", "sampling"]);
        _httpCorrelationProtocol = DefineProperty<string>(nameof(HttpCorrelationProtocol), ["properties", "httpCorrelationProtocol"]);
        _logClientIp = DefineProperty<bool>(nameof(LogClientIp), ["properties", "logClientIp"]);
        _verbosity = DefineProperty<string>(nameof(Verbosity), ["properties", "verbosity"]);
        _operationNameFormat = DefineProperty<string>(nameof(OperationNameFormat), ["properties", "operationNameFormat"]);
        _metrics = DefineProperty<bool>(nameof(Metrics), ["properties", "metrics"]);
        _parent = DefineResource<TParent>(nameof(Parent), ["parent"], isRequired: true);
    }
}

internal sealed class ApiManagementServiceDiagnosticProvisioningResource(string bicepIdentifier)
    : ApiManagementDiagnosticProvisioningResource<ApiManagementServiceProvisioningResource>(
        bicepIdentifier,
        "Microsoft.ApiManagement/service/diagnostics");

internal sealed class ApiManagementApiDiagnosticProvisioningResource(string bicepIdentifier)
    : ApiManagementDiagnosticProvisioningResource<ApiManagementApiProvisioningResource>(
        bicepIdentifier,
        "Microsoft.ApiManagement/service/apis/diagnostics");

internal sealed class ApiManagementSamplingProvisioningModel : ProvisionableConstruct
{
    private BicepValue<string>? _samplingType;
    private BicepValue<double>? _percentage;

    public BicepValue<string> SamplingType
    {
        get { Initialize(); return _samplingType!; }
        set { Initialize(); _samplingType!.Assign(value); }
    }

    public BicepValue<double> Percentage
    {
        get { Initialize(); return _percentage!; }
        set { Initialize(); _percentage!.Assign(value); }
    }

    protected override void DefineProvisionableProperties()
    {
        _samplingType = DefineProperty<string>(nameof(SamplingType), ["samplingType"], isRequired: true);
        _percentage = DefineProperty<double>(nameof(Percentage), ["percentage"], isRequired: true);
    }
}
