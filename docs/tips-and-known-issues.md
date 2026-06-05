# Tips and known issues

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
