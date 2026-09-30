(() => {
    const dataElement = document.getElementById('statistics-data');
    if (!dataElement) return;

    const status = document.getElementById('statistics-chart-status');
    if (typeof Chart === 'undefined') {
        status.hidden = false;
        return;
    }

    // Wrap labels for narrow screens; tables retain the full original names.
    const wrapLabel = label => label.match(/.{1,18}(?:\s|$)|.{1,18}/gu).map(part => part.trim());
    try {
        const data = JSON.parse(dataElement.textContent);
        const labels = data.categories.map(item => wrapLabel(item.label));
        const categoryContainer = document.getElementById('category-chart-container');
        // Category ticks are equally spaced, so every row must fit the longest label.
        const rowHeight = Math.max(...labels.map(lines => lines.length)) * 18 + 24;
        categoryContainer.style.height = `${Math.max(280, labels.length * rowHeight + 70)}px`;
        categoryContainer.hidden = false;
        document.getElementById('time-chart-container').hidden = false;

        const options = horizontal => ({
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            indexAxis: horizontal ? 'y' : 'x',
            plugins: { legend: { display: false } },
            scales: {
                [horizontal ? 'x' : 'y']: {
                    beginAtZero: true,
                    ticks: { precision: 0 },
                    title: { display: true, text: 'Recipes' }
                },
                [horizontal ? 'y' : 'x']: { ticks: { autoSkip: false, font: { size: 12, lineHeight: 1.5 } } }
            }
        });

        new Chart(document.getElementById('category-chart'), {
            type: 'bar',
            data: {
                labels,
                datasets: [{ label: 'Recipes', data: data.categories.map(item => item.count), backgroundColor: '#17674f' }]
            },
            options: options(true)
        });
        new Chart(document.getElementById('time-chart'), {
            type: 'bar',
            data: {
                labels: data.cookingTimes.map(item => wrapLabel(item.label)),
                datasets: [{ label: 'Recipes', data: data.cookingTimes.map(item => item.count), backgroundColor: '#286f9b' }]
            },
            options: options(false)
        });
    } catch {
        document.querySelectorAll('.statistics-chart').forEach(container => { container.hidden = true; });
        status.hidden = false;
    }
})();
