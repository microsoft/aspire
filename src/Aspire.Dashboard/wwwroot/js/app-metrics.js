import './plotly-basic-4.0.0-rc.0.min.js'

export function initializeChart(id, traces, exemplarTrace, rangeStartTime, rangeEndTime, serverLocale, chartInterop) {
    registerLocale(serverLocale);

    var chartContainerDiv = document.getElementById(id);
    if (!chartContainerDiv) {
        console.log(`Couldn't find container '${id}' when initializing chart.`);
        return;
    }

    // Reusing a div can create issues with chart lines appearing beyond the end range.
    // Workaround this issue by replacing the chart div. Ensures we start from a new state.
    var chartDiv = document.createElement("div");
    chartContainerDiv.replaceChildren(chartDiv);

    var themeColors = getThemeColors();

    var data = [];
    for (var i = 0; i < traces.length; i++) {
        var name = traces[i].name || "Value";
        // Colors are assigned by slot in a fixed order so a series keeps its color across re-renders.
        var color = themeColors.series[i % themeColors.series.length];
        var t = {
            x: traces[i].x,
            y: traces[i].y,
            name: name,
            text: traces[i].tooltips,
            hoverinfo: 'text',
            stackgroup: "one",
            line: {
                color: color,
                width: 2,
                // Spline gives smooth curves instead of jagged straight segments between ticks.
                // Smoothing is kept moderate (max is 1.3) so curves don't visibly overshoot the
                // actual values, e.g. dipping below zero between two zero-valued points.
                shape: 'spline',
                smoothing: 0.4
            },
            // A translucent fill keeps the stacked areas readable without overpowering the lines.
            fillcolor: withAlpha(color, themeColors.isDark ? 0.22 : 0.16)
        };
        data.push(t);
    }

    var points = {
        x: exemplarTrace.x,
        y: exemplarTrace.y,
        name: exemplarTrace.name,
        text: exemplarTrace.tooltips,
        hoverinfo: 'text',
        traceData: exemplarTrace.traceData,
        mode: 'markers',
        type: 'scatter',
        marker: {
            size: 9,
            color: themeColors.pointColor,
            // A ring in the surface color separates the marker from the lines and fills underneath.
            line: {
                color: themeColors.backgroundColor,
                width: 2
            }
        },
        hoverlabel: {
            bgcolor: themeColors.pointColor,
            bordercolor: themeColors.pointColor,
            font: { color: themeColors.pointTextColor }
        }
    };
    data.push(points);

    // Width and height are set using ResizeObserver + ploty resize call.
    var layout = {
        paper_bgcolor: themeColors.backgroundColor,
        plot_bgcolor: themeColors.backgroundColor,
        font: {
            family: themeColors.fontFamily,
            color: themeColors.textColor
        },
        // Top margin leaves room for the legend, which sits above the plot area.
        margin: { t: 36, r: 16, b: 36, l: 56 },
        xaxis: getXAxisLayout(themeColors, rangeStartTime, rangeEndTime),
        yaxis: {
            rangemode: "tozero",
            fixedrange: true,
            automargin: true,
            nticks: 6,
            // Recessive dotted gridlines: they guide the eye without competing with the data.
            showgrid: true,
            gridcolor: themeColors.gridColor,
            gridwidth: 1,
            griddash: 'dot',
            zeroline: false,
            showline: false,
            ticks: '',
            ticklen: 0,
            tickfont: { color: themeColors.mutedTextColor, size: 11 }
        },
        hovermode: "closest",
        hoverlabel: {
            bgcolor: themeColors.surfaceColor,
            bordercolor: themeColors.strokeColor,
            font: {
                family: themeColors.fontFamily,
                color: themeColors.textColor,
                size: 12
            },
            align: 'left'
        },
        showlegend: true,
        legend: {
            orientation: "h",
            x: 0,
            xanchor: "left",
            y: 1.02,
            yanchor: "bottom",
            bgcolor: 'rgba(0,0,0,0)',
            font: {
                color: themeColors.textColor,
                size: 12
            },
            itemsizing: 'constant',
            itemwidth: 30,
            traceorder: "normal",
            itemclick: false,
            itemdoubleclick: false
        }
    };

    // Plotly 4.x flipped the `showSendToCloud` default to true and points `plotlyServerURL`
    // at https://cloud.plotly.com/newchart. That surfaces a "Share Chart" modebar button which
    // uploads the full chart JSON — i.e. the user's telemetry — to a third-party service.
    // `displayModeBar: false` already hides the button today, but these are set explicitly so
    // enabling the modebar later can never silently re-introduce outbound data egress.
    // Neither window.open nor postMessage is governed by our CSP, so this is the only guard.
    // See https://github.com/plotly/plotly.js/blob/master/CHANGELOG.md (v4.0.0 breaking changes).
    var options = {
        scrollZoom: false,
        displayModeBar: false,
        showSendToCloud: false,
        plotlyServerURL: ''
    };

    var plot = Plotly.newPlot(chartDiv, data, layout, options);

    fixTraceLineRendering(chartDiv);

    // We only want a pointer cursor when the mouse is hovering over an exemplar point.
    // Set the drag layer cursor back to the default and then use plotly_hover/ploty_unhover events to set to pointer.
    var dragLayer = document.getElementsByClassName('nsewdrag')[0];
    dragLayer.style.cursor = 'default';

    // Use mousedown instead of plotly_click event because plotly_click has issues with updating charts.
    // The current point is tracked by setting it with hover/unhover events and then mousedown uses the current value.
    var currentPoint = null;
    chartDiv.on('plotly_hover', function (data) {
        var point = data.points[0];
        if (point.fullData.name == exemplarTrace.name) {
            currentPoint = point;
            dragLayer.style.cursor = 'pointer';
        }
    });
    chartDiv.on('plotly_unhover', function (data) {
        var point = data.points[0];
        if (point.fullData.name == exemplarTrace.name) {
            currentPoint = null;
            dragLayer.style.cursor = 'default';
        }
    });
    chartDiv.addEventListener("mousedown", (e) => {
        if (currentPoint) {
            var point = currentPoint;
            var pointTraceData = point.data.traceData[point.pointIndex];

            var traceId = pointTraceData.traceId;
            var spanId = pointTraceData.spanId;

            // If the exemplar has trace and span details then navigate to the span on click.
            if (traceId && spanId) {
                chartInterop.invokeMethodAsync('ViewSpan', traceId, spanId);
            }
        }
    });

    const resizeObserver = new ResizeObserver(entries => {
        for (let entry of entries) {
            // Don't resize if not visible.
            var display = window.getComputedStyle(entry.target).display;
            var isHidden = !display || display === "none";
            if (!isHidden) {
                Plotly.Plots.resize(entry.target);
            }
        }
    });
    plot.then(plotyDiv => {
        resizeObserver.observe(plotyDiv);
    });
}

export function updateChart(id, traces, exemplarTrace, rangeStartTime, rangeEndTime) {
    var chartContainerDiv = document.getElementById(id);
    if (!chartContainerDiv) {
        console.log(`Couldn't find container '${id}' when updating chart.`);
        return;
    }

    var chartDiv = chartContainerDiv.firstChild;
    if (!chartDiv) {
        console.log(`Couldn't find div inside container '${id}' when updating chart. Chart may not have been successfully initialized.`);
        return;
    }

    var themeColors = getThemeColors();

    var xUpdate = [];
    var yUpdate = [];
    var tooltipsUpdate = [];
    var traceData = [];
    for (var i = 0; i < traces.length; i++) {
        xUpdate.push(traces[i].x);
        yUpdate.push(traces[i].y);
        tooltipsUpdate.push(traces[i].tooltips);
        traceData.push(traces.traceData);
    }

    xUpdate.push(exemplarTrace.x);
    yUpdate.push(exemplarTrace.y);
    tooltipsUpdate.push(exemplarTrace.tooltips);
    traceData.push(exemplarTrace.traceData);

    var data = {
        x: xUpdate,
        y: yUpdate,
        text: tooltipsUpdate,
        traceData: traceData
    };

    var layout = {
        xaxis: getXAxisLayout(themeColors, rangeStartTime, rangeEndTime)
    };

    Plotly.update(chartDiv, data, layout);

    fixTraceLineRendering(chartDiv);
}

function getXAxisLayout(themeColors, rangeStartTime, rangeEndTime) {
    return {
        type: 'date',
        range: [rangeEndTime, rangeStartTime],
        fixedrange: true,
        tickformat: "%X",
        showgrid: false,
        zeroline: false,
        showline: true,
        linecolor: themeColors.strokeColor,
        ticks: 'outside',
        ticklen: 4,
        tickcolor: themeColors.strokeColor,
        tickfont: { color: themeColors.mutedTextColor, size: 11 },
        // Vertical crosshair at the hovered point makes it easy to read the time against the axis.
        showspikes: true,
        spikemode: 'across',
        spikesnap: 'data',
        spikethickness: 1,
        spikedash: 'dot',
        spikecolor: themeColors.mutedTextColor
    };
}

// Categorical series palette, validated for color-vision deficiency separation on adjacent
// series in both modes. The dark column is the same hues stepped for a dark surface rather
// than an automatic inversion. Order matters: it's what keeps adjacent series distinguishable.
const lightSeriesColors = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'];
const darkSeriesColors = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'];

function getThemeColors() {
    // Get colors from the current light/dark theme.
    var style = getComputedStyle(document.body);
    var isDark = document.documentElement.dataset.theme === "dark";
    return {
        isDark: isDark,
        series: isDark ? darkSeriesColors : lightSeriesColors,
        backgroundColor: style.getPropertyValue("--colorNeutralBackground2").trim(),
        surfaceColor: style.getPropertyValue("--colorNeutralBackground1").trim(),
        textColor: style.getPropertyValue("--colorNeutralForeground1").trim(),
        mutedTextColor: style.getPropertyValue("--colorNeutralForeground3").trim(),
        gridColor: style.getPropertyValue("--colorNeutralStroke2").trim(),
        strokeColor: style.getPropertyValue("--colorNeutralStroke1").trim(),
        pointColor: style.getPropertyValue("--colorBrandBackground").trim(),
        pointTextColor: style.getPropertyValue("--colorNeutralForegroundOnBrand").trim() || "#ffffff",
        fontFamily: style.fontFamily
    };
}

function withAlpha(hexColor, alpha) {
    // Expects '#rrggbb' from the series palette and returns an rgba() string for translucent fills.
    var r = parseInt(hexColor.substring(1, 3), 16);
    var g = parseInt(hexColor.substring(3, 5), 16);
    var b = parseInt(hexColor.substring(5, 7), 16);
    return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function fixTraceLineRendering(chartDiv) {
    // In stack area charts Plotly orders traces so the top line area overwrites the line of areas below it.
    // This isn't the effect we want. When the P50, P90 and P99 values are the same, the line displayed is P99
    // on the P50 area.
    //
    // The fix is to reverse the order of traces so the correct line is on top. There isn't a way to do this
    // with CSS because SVG doesn't support z-index. Node order is what determines the rendering order.
    //
    // https://github.com/plotly/plotly.js/issues/6579
    var parent = chartDiv.querySelector(".scatterlayer");

    if (parent.childNodes.length > 0) {
        for (var i = 1; i < parent.childNodes.length; i++) {
            var child = parent.childNodes[i];
            parent.insertBefore(child, parent.firstChild);
        }

        // Check if there is a trace with points. It should be top most.
        for (var i = 0; i < parent.childNodes.length; i++) {
            var child = parent.childNodes[i];
            if (child.querySelector(".point")) {
                // Append trace to parent to move to the last in the collection.
                parent.appendChild(child);
            }
        }
    }
}

function registerLocale(serverLocale) {
    // Register the locale for Plotly.js. This is to enable localization of time format shown by the charts.
    // Changing plotly.js time formatting is better than supplying values from the server which is very difficult to do correctly.

    // Right now necessary changes are to:
    // -Update AM/PM
    // -Update time format to 12/24 hour.
    var locale = {
        moduleType: 'locale',
        name: 'en',
        dictionary: {
            'Click to enter Colorscale title': 'Click to enter Colourscale title'
        },
        format: {
            days: ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
            shortDays: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'],
            months: ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'],
            shortMonths: ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'],
            periods: serverLocale.periods,
            dateTime: '%a %b %e %X %Y',
            date: '%d/%m/%Y',
            time: serverLocale.time,
            decimal: '.',
            thousands: ',',
            grouping: [3],
            currency: ['$', ''],
            year: '%Y',
            month: '%b %Y',
            dayMonth: '%b %-d',
            dayMonthYear: '%b %-d, %Y'
        }
    };
    Plotly.register(locale);
}
