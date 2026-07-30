/*
 Announces row text for the specified row keys to screen readers using an offscreen div.
 Row keys are timestamp ticks that match each row's `data-row-time` attribute. Resolving rows by
 key (rather than DOM index) is required because the table is virtualized, so only on-screen rows
 exist in the DOM; a key that isn't currently rendered is simply skipped.
 */
export function announceDataGridRows(dataGridContainerId, rowKeys) {
    const containerId = "table-announce-container";
    let container = document.getElementById(containerId);
    if (container === null) {
        container = document.createElement("div");
        container.setAttribute("id", containerId);
        container.setAttribute("class", "visually-hidden");
        container.setAttribute("role", "log");

        const list = document.createElement("ul");
        container.appendChild(list);
        document.body.appendChild(container);
    }

    const list = container.children[0];

    rowKeys.forEach(rowKey => {
        const rowText = getRowText(dataGridContainerId, rowKey);
        if (rowText) {
            const newItem = document.createElement("li");
            const textNode = document.createTextNode(rowText);
            newItem.appendChild(textNode);
            list.appendChild(newItem);
        }
    });
}

function getRowText(dataGridContainerId, rowKey) {
    const container = document.getElementById(dataGridContainerId);
    if (!container) {
        return null;
    }

    const row = container.querySelector(`tbody tr[data-row-time="${rowKey}"]`);
    if (!row) {
        return null;
    }

    const cells = row.getElementsByTagName("td");
    let texts = [];
    for (let i = 0; i < cells.length; i++) {
        texts.push(cells[i].textContent);
    }

    return texts.join(", ");
}
