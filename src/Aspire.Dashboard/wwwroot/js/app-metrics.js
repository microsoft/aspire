import './plotly-basic-2.35.2.min.js'

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
        var t = {
            x: traces[i].x,
            y: traces[i].y,
            name: name,
            text: traces[i].tooltips,
            hoverinfo: 'text',
            stackgroup: "one",
            line: {
                color: themeColors.seriesColors[i % themeColors.seriesColors.length],
                width: 2
            },
            fillcolor: themeColors.seriesFillColors[i % themeColors.seriesFillColors.length]
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
            size: 16,
            color: themeColors.pointColor,
            line: {
                color: themeColors.backgroundColor,
                width: 1
            }
        }
    };
    data.push(points);

    // Width and height are set using ResizeObserver + ploty resize call.
    var layout = {
        paper_bgcolor: themeColors.backgroundColor,
        plot_bgcolor: themeColors.backgroundColor,
        margin: { t: 0, r: 0, b: 40, l: 50 },
        xaxis: {
            type: 'date',
            range: [rangeEndTime, rangeStartTime],
            fixedrange: true,
            tickformat: "%X",
            color: themeColors.axisColor,
            gridcolor: themeColors.gridColor,
            linecolor: themeColors.gridColor,
            zerolinecolor: themeColors.gridColor
        },
        yaxis: {
            rangemode: "tozero",
            fixedrange: true,
            color: themeColors.axisColor,
            gridcolor: themeColors.gridColor,
            linecolor: themeColors.gridColor,
            zerolinecolor: themeColors.gridColor
        },
        hovermode: "closest",
        showlegend: true,
        legend: {
            orientation: "h",
            font: {
                color: themeColors.textColor
            },
            traceorder: "normal",
            itemclick: false,
            itemdoubleclick: false
        }
    };

    var options = { scrollZoom: false, displayModeBar: false };

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
        xaxis: {
            type: 'date',
            range: [rangeEndTime, rangeStartTime],
            fixedrange: true,
            tickformat: "%X",
            color: themeColors.axisColor,
            gridcolor: themeColors.gridColor,
            linecolor: themeColors.gridColor,
            zerolinecolor: themeColors.gridColor
        },
        yaxis: {
            color: themeColors.axisColor,
            gridcolor: themeColors.gridColor,
            linecolor: themeColors.gridColor,
            zerolinecolor: themeColors.gridColor
        }
    };

    Plotly.update(chartDiv, data, layout);

    fixTraceLineRendering(chartDiv);
}

function getThemeColors() {
    // Get colors from the current light/dark theme.
    var style = getComputedStyle(document.body);
    return {
        backgroundColor: style.getPropertyValue("--aspire-chart-background").trim() || style.getPropertyValue("--fill-color"),
        textColor: style.getPropertyValue("--neutral-foreground-rest"),
        axisColor: style.getPropertyValue("--aspire-chart-axis-color").trim() || style.getPropertyValue("--neutral-foreground-rest"),
        gridColor: style.getPropertyValue("--aspire-chart-grid-color").trim() || style.getPropertyValue("--neutral-stroke-divider-rest"),
        pointColor: style.getPropertyValue("--accent-fill-rest"),
        seriesColors: [
            style.getPropertyValue("--aspire-chart-series-1").trim(),
            style.getPropertyValue("--aspire-chart-series-2").trim(),
            style.getPropertyValue("--aspire-chart-series-3").trim()
        ],
        seriesFillColors: [
            style.getPropertyValue("--aspire-chart-series-1-fill").trim(),
            style.getPropertyValue("--aspire-chart-series-2-fill").trim(),
            style.getPropertyValue("--aspire-chart-series-3-fill").trim()
        ]
    };
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
