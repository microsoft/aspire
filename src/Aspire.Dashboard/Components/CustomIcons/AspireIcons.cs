// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.CustomIcons;

internal static class AspireIcons
{
    internal static class Size16
    {
        internal sealed class VisualStudio : Icon { public VisualStudio() : base("VisualStudio", IconVariant.Regular, IconSize.Size16,
            """
            <g transform="scale(0.3)">
              <path d="M 35.445312 2.0117188 C 35.056812 2.0253438 34.669266 2.1265938 34.322266 2.3085938 C 35.227266 2.7565937 35.865703 3.6414062 35.970703 4.6914062 C 35.988703 4.8174062 36 4.9322812 36 4.9882812 L 36 44.988281 C 36 46.174281 35.306594 47.190734 34.308594 47.677734 C 34.672594 47.876734 35.079 47.988281 35.5 47.988281 C 35.873 47.988281 36.224969 47.901859 36.542969 47.755859 L 36.544922 47.759766 C 38.152922 46.927766 46.192281 42.764703 46.488281 42.595703 C 47.421281 42.060703 48 41.061281 48 39.988281 L 48 9.9882812 C 48 8.9902813 47.505734 8.0619062 46.677734 7.5039062 C 46.357734 7.2869063 36.582031 2.2363281 36.582031 2.2363281 L 36.580078 2.2402344 C 36.224078 2.0727344 35.833813 1.9980938 35.445312 2.0117188 z M 33 3.9882812 C 32.744125 3.9882812 32.487969 4.08575 32.292969 4.28125 C 32.292969 4.28125 26.568469 10.718844 20.230469 17.839844 L 27.724609 23.970703 L 34 18.685547 L 34 4.9882812 C 34 4.7322812 33.902031 4.47625 33.707031 4.28125 C 33.511531 4.08575 33.255875 3.9882812 33 3.9882812 z M 8 10.988281 C 7.844 10.988281 7.5538594 10.988844 6.2558594 11.589844 C 5.5408594 11.920844 3.0507812 13.228516 3.0507812 13.228516 C 2.7207812 13.407516 2.4545781 13.687719 2.2675781 14.011719 C 2.3435781 13.999719 2.4190469 13.988281 2.4980469 13.988281 L 2.5 13.988281 C 3.168 13.988281 3.5776562 14.454469 3.7226562 14.605469 C 3.7226562 14.605469 31.929969 45.332313 32.292969 45.695312 C 32.487969 45.890313 32.742047 45.988281 32.998047 45.988281 C 33.254047 45.988281 33.510078 45.890313 33.705078 45.695312 C 33.900078 45.500312 34 45.244281 34 44.988281 L 34 31.658203 C 34 31.658203 9.53375 11.661813 9.34375 11.507812 C 8.97575 11.173813 8.497 10.988281 8 10.988281 z M 2.1992188 15.988281 C 2.0892188 15.988281 2 16.078453 2 16.189453 L 2 34.794922 C 2 34.901922 2.0873125 34.990234 2.1953125 34.990234 C 2.2513125 34.990234 2.3009375 34.964781 2.3359375 34.925781 L 8 28.638672 L 8 22.007812 L 2.3417969 16.046875 C 2.3057969 16.010875 2.2542188 15.988281 2.1992188 15.988281 z M 12.253906 26.802734 C 12.253906 26.802734 3.6585469 36.452781 3.5605469 36.550781 C 3.2895469 36.821781 2.915 36.988281 2.5 36.988281 C 2.422 36.988281 2.3464844 36.978797 2.2714844 36.966797 C 2.4734844 37.317797 2.7697188 37.607156 3.1367188 37.785156 L 3.1328125 37.791016 C 4.0028125 38.233016 6.8739531 39.691703 7.2519531 39.845703 C 7.4899531 39.940703 7.741 39.988281 8 39.988281 C 8.347 39.988281 8.6900469 39.898656 8.9980469 39.722656 C 9.0210469 39.709656 17.527344 32.552734 17.527344 32.552734 L 12.253906 26.802734 z"/>
            </g>
            """) { } };
        internal sealed class VSCode : Icon { public VSCode() : base("VSCode", IconVariant.Regular, IconSize.Size16,
            """
            <g transform="scale(0.45)">
              <path d="M30.865 3.448l-6.583-3.167c-0.766-0.37-1.677-0.214-2.276 0.385l-12.609 11.505-5.495-4.167c-0.51-0.391-1.229-0.359-1.703 0.073l-1.76 1.604c-0.583 0.526-0.583 1.443-0.005 1.969l4.766 4.349-4.766 4.349c-0.578 0.526-0.578 1.443 0.005 1.969l1.76 1.604c0.479 0.432 1.193 0.464 1.703 0.073l5.495-4.172 12.615 11.51c0.594 0.599 1.505 0.755 2.271 0.385l6.589-3.172c0.693-0.333 1.13-1.031 1.13-1.802v-21.495c0-0.766-0.443-1.469-1.135-1.802zM24.005 23.266l-9.573-7.266 9.573-7.266z"/>
            </g>
            """) { } };
    }

    internal static class Size24
    {
        // The official SVGs from GitHub have a viewbox of 96x96, so we need to scale them down to 20x20 and center them within the 24x24 box to make them match the
        // other icons we're using. We also need to remove the fill attribute from the SVGs so that we can color them with CSS.
        internal sealed class GitHub : Icon { public GitHub() : base("GitHub", IconVariant.Regular, IconSize.Size24, @"<path transform=""scale(0.20833) translate(9.6 9.6)"" fill-rule=""evenodd"" clip-rule=""evenodd"" d=""M48.854 0C21.839 0 0 22 0 49.217c0 21.756 13.993 40.172 33.405 46.69 2.427.49 3.316-1.059 3.316-2.362 0-1.141-.08-5.052-.08-9.127-13.59 2.934-16.42-5.867-16.42-5.867-2.184-5.704-5.42-7.17-5.42-7.17-4.448-3.015.324-3.015.324-3.015 4.934.326 7.523 5.052 7.523 5.052 4.367 7.496 11.404 5.378 14.235 4.074.404-3.178 1.699-5.378 3.074-6.6-10.839-1.141-22.243-5.378-22.243-24.283 0-5.378 1.94-9.778 5.014-13.2-.485-1.222-2.184-6.275.486-13.038 0 0 4.125-1.304 13.426 5.052a46.97 46.97 0 0 1 12.214-1.63c4.125 0 8.33.571 12.213 1.63 9.302-6.356 13.427-5.052 13.427-5.052 2.67 6.763.97 11.816.485 13.038 3.155 3.422 5.015 7.822 5.015 13.2 0 18.905-11.404 23.06-22.324 24.283 1.78 1.548 3.316 4.481 3.316 9.126 0 6.6-.08 11.897-.08 13.526 0 1.304.89 2.853 3.316 2.364 19.412-6.52 33.405-24.935 33.405-46.691C97.707 22 75.788 0 48.854 0z"" />") { } }
        internal sealed class Logo : Icon { public Logo() : base("Logo", IconVariant.Regular, IconSize.Size24, @"<svg width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" xmlns=""http://www.w3.org/2000/svg"">
    <mask id=""mask0_449_831"" style=""mask-type:alpha"" maskUnits=""userSpaceOnUse"" x=""0"" y=""0"" width=""24"" height=""22"">
        <path fill-rule=""evenodd"" clip-rule=""evenodd"" d=""M5.39001 12C4.49001 12 3.67 12.4799 3.22 13.2499L6.67 7.27994L6.6817 7.25982L9.84001 1.79005C10.05 1.43005 10.36 1.11005 10.75 0.880049C11.14 0.650049 11.57 0.550049 12 0.550049C12.86 0.550049 13.7 0.990049 14.17 1.80005L17.33 7.28005L23.67 18.25C23.88 18.62 24 19.05 24 19.5C24 20.88 22.88 22 21.5 22H8.27002C8.27001 22 8.27002 22 8.27002 22H2.5C1.12 22 0 20.88 0 19.5C0 19.05 0.12 18.62 0.33 18.25L3.22 13.2499C3.67 12.4799 4.49001 12 5.39001 12C5.39002 12 5.39001 12 5.39001 12Z"" fill=""url(#paint0_linear_449_831)""/>
    </mask>
    <g mask=""url(#mask0_449_831)"">
        <path d=""M20.06 12H13.72L11 7.28005C10.79 6.91005 10.48 6.59005 10.08 6.37005C8.88998 5.67005 7.35998 6.08005 6.66998 7.28005L9.83998 1.79005C10.05 1.43005 10.36 1.11005 10.75 0.880049C11.14 0.650049 11.57 0.550049 12 0.550049C12.86 0.550049 13.7 0.990049 14.17 1.80005L17.33 7.28005L20.06 12Z"" fill=""url(#paint1_linear_449_831)""/>
        <g filter=""url(#filter0_dd_449_831)"">
            <path d=""M5.38997 11.9999H13.72L11 7.27994C10.79 6.90994 10.48 6.58994 10.08 6.36994C8.88997 5.66994 7.35997 6.07994 6.66997 7.27994L3.21997 13.2499C3.66997 12.4799 4.48997 11.9999 5.38997 11.9999Z"" fill=""url(#paint2_linear_449_831)""/>
            <path d=""M21.5 22C22.88 22 24 20.88 24 19.5C24 19.05 23.88 18.62 23.67 18.25L20.06 12L13.72 11.9999L17.33 18.25C17.55 18.62 17.67 19.05 17.67 19.5C17.67 20.88 16.55 22 15.17 22H21.5Z"" fill=""url(#paint3_linear_449_831)""/>
        </g>
        <g filter=""url(#filter1_dd_449_831)"">
            <path d=""M17.67 19.5C17.67 20.88 16.55 22 15.17 22H8.27002C9.65002 22 10.77 20.88 10.77 19.5C10.77 19.05 10.65 18.62 10.44 18.25L7.55001 13.25C7.52002 13.19 7.48001 13.14 7.44001 13.08C6.99001 12.42 6.23001 12 5.39001 12H13.72L17.33 18.25C17.55 18.62 17.67 19.05 17.67 19.5Z"" fill=""url(#paint4_linear_449_831)""/>
        </g>
        <g filter=""url(#filter2_dd_449_831)"">
            <path d=""M10.77 19.5C10.77 20.88 9.65 22 8.27 22H2.5C1.12 22 0 20.88 0 19.5C0 19.05 0.12 18.62 0.33 18.25L3.22 13.25C3.67 12.48 4.49 12 5.39 12C6.23 12 6.99 12.42 7.44 13.08C7.48 13.14 7.52 13.19 7.55 13.25L10.44 18.25C10.65 18.62 10.77 19.05 10.77 19.5Z"" fill=""url(#paint5_linear_449_831)""/>
        </g>
    </g>
    <defs>
        <filter id=""filter0_dd_449_831"" x=""1.21997"" y=""4.52808"" width=""24.78"" height=""19.9719"" filterUnits=""userSpaceOnUse"" color-interpolation-filters=""sRGB"">
            <feFlood flood-opacity=""0"" result=""BackgroundImageFix""/>
            <feColorMatrix in=""SourceAlpha"" type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"" result=""hardAlpha""/>
            <feOffset dy=""0.095""/>
            <feGaussianBlur stdDeviation=""0.095""/>
            <feColorMatrix type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0.24 0""/>
            <feBlend mode=""normal"" in2=""BackgroundImageFix"" result=""effect1_dropShadow_449_831""/>
            <feColorMatrix in=""SourceAlpha"" type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"" result=""hardAlpha""/>
            <feOffset dy=""0.5""/>
            <feGaussianBlur stdDeviation=""1""/>
            <feColorMatrix type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0.32 0""/>
            <feBlend mode=""normal"" in2=""effect1_dropShadow_449_831"" result=""effect2_dropShadow_449_831""/>
            <feBlend mode=""normal"" in=""SourceGraphic"" in2=""effect2_dropShadow_449_831"" result=""shape""/>
        </filter>
        <filter id=""filter1_dd_449_831"" x=""3.39001"" y=""10.5"" width=""16.28"" height=""14"" filterUnits=""userSpaceOnUse"" color-interpolation-filters=""sRGB"">
            <feFlood flood-opacity=""0"" result=""BackgroundImageFix""/>
            <feColorMatrix in=""SourceAlpha"" type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"" result=""hardAlpha""/>
            <feOffset dy=""0.095""/>
            <feGaussianBlur stdDeviation=""0.095""/>
            <feColorMatrix type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0.24 0""/>
            <feBlend mode=""normal"" in2=""BackgroundImageFix"" result=""effect1_dropShadow_449_831""/>
            <feColorMatrix in=""SourceAlpha"" type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"" result=""hardAlpha""/>
            <feOffset dy=""0.5""/>
            <feGaussianBlur stdDeviation=""1""/>
            <feColorMatrix type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0.32 0""/>
            <feBlend mode=""normal"" in2=""effect1_dropShadow_449_831"" result=""effect2_dropShadow_449_831""/>
            <feBlend mode=""normal"" in=""SourceGraphic"" in2=""effect2_dropShadow_449_831"" result=""shape""/>
        </filter>
        <filter id=""filter2_dd_449_831"" x=""-2"" y=""10.5"" width=""14.77"" height=""14"" filterUnits=""userSpaceOnUse"" color-interpolation-filters=""sRGB"">
            <feFlood flood-opacity=""0"" result=""BackgroundImageFix""/>
            <feColorMatrix in=""SourceAlpha"" type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"" result=""hardAlpha""/>
            <feOffset dy=""0.095""/>
            <feGaussianBlur stdDeviation=""0.095""/>
            <feColorMatrix type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0.24 0""/>
            <feBlend mode=""normal"" in2=""BackgroundImageFix"" result=""effect1_dropShadow_449_831""/>
            <feColorMatrix in=""SourceAlpha"" type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"" result=""hardAlpha""/>
            <feOffset dy=""0.5""/>
            <feGaussianBlur stdDeviation=""1""/>
            <feColorMatrix type=""matrix"" values=""0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0.32 0""/>
            <feBlend mode=""normal"" in2=""effect1_dropShadow_449_831"" result=""effect2_dropShadow_449_831""/>
            <feBlend mode=""normal"" in=""SourceGraphic"" in2=""effect2_dropShadow_449_831"" result=""shape""/>
        </filter>
        <linearGradient id=""paint0_linear_449_831"" x1=""1.88475"" y1=""11.1667"" x2=""10.31"" y2=""23.1443"" gradientUnits=""userSpaceOnUse"">
            <stop stop-color=""#CBBFF2""/>
            <stop offset=""1"" stop-color=""#B9AAEE""/>
        </linearGradient>
        <linearGradient id=""paint1_linear_449_831"" x1=""9.6127"" y1=""-0.685575"" x2=""16.8764"" y2=""13.8912"" gradientUnits=""userSpaceOnUse"">
            <stop stop-color=""#7455DD""/>
            <stop stop-color=""#6745DA""/>
            <stop offset=""1"" stop-color=""#512BD4""/>
        </linearGradient>
        <linearGradient id=""paint2_linear_449_831"" x1=""7.90532"" y1=""3.78438"" x2=""19.1767"" y2=""23.0023"" gradientUnits=""userSpaceOnUse"">
            <stop stop-color=""#856AE1""/>
            <stop offset=""1"" stop-color=""#7455DD""/>
        </linearGradient>
        <linearGradient id=""paint3_linear_449_831"" x1=""7.90532"" y1=""3.78438"" x2=""19.1767"" y2=""23.0023"" gradientUnits=""userSpaceOnUse"">
            <stop stop-color=""#856AE1""/>
            <stop offset=""1"" stop-color=""#7455DD""/>
        </linearGradient>
        <linearGradient id=""paint4_linear_449_831"" x1=""5.4257"" y1=""9.22222"" x2=""13.2216"" y2=""21.4193"" gradientUnits=""userSpaceOnUse"">
            <stop stop-color=""#A895E9""/>
            <stop offset=""1"" stop-color=""#9780E5""/>
        </linearGradient>
        <linearGradient id=""paint5_linear_449_831"" x1=""1.88475"" y1=""11.1667"" x2=""10.31"" y2=""23.1443"" gradientUnits=""userSpaceOnUse"">
            <stop stop-color=""#CBBFF2""/>
            <stop offset=""1"" stop-color=""#B9AAEE""/>
        </linearGradient>
    </defs>
</svg>
") { } }
    }

}
