// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only.

internal sealed class IFrameInteractionPages
{
    private readonly EndpointReference _frontendEndpoint;
    private readonly EndpointReference? _pgAdminEndpoint;

    public IFrameInteractionPages(EndpointReference frontendEndpoint, EndpointReference? pgAdminEndpoint)
    {
        _frontendEndpoint = frontendEndpoint;
        _pgAdminEndpoint = pgAdminEndpoint;
    }

    public void Register(IDistributedApplicationBuilder builder)
    {
        builder.OnBeforeStart((@event, ct) =>
        {
            var interactionService = @event.Services.GetRequiredService<IInteractionService>();

            interactionService.RegisterPage("frontend-app", new IFramePageOptions
            {
                Title = "Online Store",
                IFrameEndpoint = _frontendEndpoint
            });

            interactionService.RegisterMenuButton(new MenuButtonOptions
            {
                IconName = "Globe",
                Text = "Online Store",
                Url = "/pages/frontend-app"
            });

            if (_pgAdminEndpoint is not null)
            {
                interactionService.RegisterPage("pgadmin", new IFramePageOptions
                {
                    Title = "PG Admin",
                    IFrameEndpoint = _pgAdminEndpoint
                });

                interactionService.RegisterMenuButton(new MenuButtonOptions
                {
                    IconName = "Database",
                    Text = "PG Admin",
                    Url = "/pages/pgadmin"
                });
            }

            return Task.CompletedTask;
        });
    }
}
