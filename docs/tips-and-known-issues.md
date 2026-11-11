# Tips and known issues

## Aspire 17: intentionally empty parameter values

Aspire 17 treats `null` and `""` as missing values for required parameters. Parameters are required by default. This is an intentional breaking change introduced by [#18108](https://github.com/microsoft/aspire/pull/18108); an explicit empty default no longer satisfies a required parameter.

If an empty value is valid for your application, mark that parameter optional:

```typescript
const selector = await (await builder.addParameter('optional-selector', { value: '' }))
    .withOptional();
await api.withEnvironment('OPTIONAL_SELECTOR', selector);
```

In C#, use `builder.AddParameter("optional-selector", "").WithOptional()`.

An optional parameter with an explicit empty value sets `OPTIONAL_SELECTOR` to `""`. An optional parameter with no configured value resolves to `null`, so the environment variable is omitted instead. In TypeScript, omitting `value` or supplying `value: null` uses parameter configuration; it does not override a configured value with an empty string.

Required validation also applies to secret and connection-string parameters, and to values supplied through `SetValueAsync` or `TrySetValue`. Do not mark required credentials optional just to suppress validation. Clearing a required parameter makes its value unavailable until a nonempty value is supplied.

When Dashboard interaction is available, a missing required startup value stays pending so you can supply it in **Parameters**. Resource state tooltips and Details identify referenced parameters without values, including those blocking a waiting dependency. Without interaction, initialization fails with a `MissingParameterValueException` naming the parameter. Configure a nonempty value and restart the AppHost, or intentionally make the parameter optional.

Optional parameters are not currently supported by the manifest publisher. Do not use `.withOptional()` as a workaround for a required deployment value.

## My containers aren't being created at all

We have seen Docker get into weird state on "DevBox" machines that get hibernated every evening. You can check if you got into this state by opening a command window and doing "docker ps". If it hangs forever without error, that is it.

The remedy is to do "Restart Docker" gesture from the Docker icon in system tray.

## If something goes wrong and you are left with a bunch of Aspire TestShop orphaned processes and containers

This will display process IDs of all the Aspire shopping running processes

```ps1
ps | where ProcessName -cmatch '(Api)|(Catalog)|(Basket)|(AppHost)|(MyFrontend)|(OrderProcessor)' | % {Write-Output $_.Id }
```

… and this will kill them

```ps1
ps | where ProcessName -cmatch '(Api)|(Catalog)|(Basket)|(AppHost)|(MyFrontend)|(OrderProcessor)' | % {Write-Output $_.Id; kill $_.Id }
```
