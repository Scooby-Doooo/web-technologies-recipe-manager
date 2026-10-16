// Recipe card on the details page: the card is drawn with the 2D canvas API and saved as PNG.
(() => {
    const canvas = document.getElementById('recipe-card-canvas');
    if (!canvas?.getContext) return;
    const recipe = JSON.parse(document.getElementById('recipe-card-data').textContent);
    const context = canvas.getContext('2d');
    const format = document.getElementById('recipe-card-format');
    const theme = document.getElementById('recipe-card-theme');
    const showIngredients = document.getElementById('recipe-card-ingredients');
    const download = document.getElementById('recipe-card-download');
    const font = getComputedStyle(document.body).fontFamily;
    const padding = 72;
    const themes = {
        green: { background: '#17674f', glow: '#2b8a6c', surface: '#1f7a5f', text: '#ffffff', muted: '#cfe7dc', accent: '#f4c95d', onAccent: '#203b36' },
        light: { background: '#f6faf7', glow: '#ffffff', surface: '#e3efe8', text: '#203b36', muted: '#566a65', accent: '#17674f', onAccent: '#ffffff' },
        dark: { background: '#16211f', glow: '#2a3d39', surface: '#22312e', text: '#f2f7f4', muted: '#9fb5ad', accent: '#7fd3b0', onAccent: '#16211f' }
    };
    let photo = null;

    function loadPhoto() {
        if (!recipe.imagePath) return Promise.resolve(null);
        const url = new URL(recipe.imagePath, location.href);
        // A picture from another site would taint the canvas, and toBlob would then refuse to export it.
        if (url.origin !== location.origin) return Promise.resolve(null);
        const image = new Image();
        image.src = url.href;
        return image.decode().then(() => image, () => null);
    }

    function setFont(weight, size) {
        context.font = `${weight} ${size}px ${font}`;
    }

    const fits = (text, width) => context.measureText(text).width <= width;

    // Splits text into lines that fit the width with the current font; the last allowed line gets "…".
    function wrap(text, width, maxLines = Infinity) {
        const lines = [];
        let line = '';
        for (const word of text.trim().split(/\s+/)) {
            const candidate = line ? `${line} ${word}` : word;
            if (fits(candidate, width)) {
                line = candidate;
                continue;
            }
            if (line) lines.push(line);
            line = word;
            while (!fits(line, width)) {
                // A word wider than the card is cut by characters.
                let cut = line.length - 1;
                while (cut > 1 && !fits(line.slice(0, cut), width)) cut--;
                lines.push(line.slice(0, cut));
                line = line.slice(cut);
            }
        }
        if (line) lines.push(line);
        if (lines.length <= maxLines) return lines;
        let last = lines[maxLines - 1];
        while (last && !fits(`${last}…`, width)) last = last.slice(0, -1);
        return [...lines.slice(0, maxLines - 1), `${last.trimEnd()}…`];
    }

    function drawCover(image, x, y, width, height) {
        const scale = Math.max(width / image.naturalWidth, height / image.naturalHeight);
        const sourceWidth = width / scale;
        const sourceHeight = height / scale;
        context.drawImage(image, (image.naturalWidth - sourceWidth) / 2, (image.naturalHeight - sourceHeight) / 2,
            sourceWidth, sourceHeight, x, y, width, height);
    }

    // Without a photo the card shows a plate with a fork and a knife.
    function drawPlate(colors, x, y, width, height) {
        const centerX = x + width / 2;
        const centerY = y + height / 2;
        const radius = Math.min(width, height) * 0.3;
        context.fillStyle = colors.surface;
        context.fillRect(x, y, width, height);
        context.strokeStyle = colors.muted;
        context.lineWidth = 10;
        context.lineCap = 'round';
        for (const r of [radius, radius * 0.7]) {
            context.beginPath();
            context.arc(centerX, centerY, r, 0, Math.PI * 2);
            context.stroke();
        }
        const forkX = centerX - radius - 60;
        context.beginPath();
        for (const offset of [-14, 0, 14]) {
            context.moveTo(forkX + offset, centerY - radius * 0.8);
            context.lineTo(forkX + offset, centerY - radius * 0.35);
        }
        context.moveTo(forkX - 14, centerY - radius * 0.35);
        context.quadraticCurveTo(forkX, centerY - radius * 0.15, forkX + 14, centerY - radius * 0.35);
        context.moveTo(forkX, centerY - radius * 0.25);
        context.lineTo(forkX, centerY + radius * 0.8);
        const knifeX = centerX + radius + 60;
        context.moveTo(knifeX, centerY + radius * 0.8);
        context.lineTo(knifeX, centerY - radius * 0.8);
        context.quadraticCurveTo(knifeX + 34, centerY - radius * 0.4, knifeX, centerY);
        context.stroke();
    }

    function drawClock(colors, x, y) {
        context.strokeStyle = colors.accent;
        context.lineWidth = 5;
        context.beginPath();
        context.arc(x, y, 17, 0, Math.PI * 2);
        context.moveTo(x, y - 10);
        context.lineTo(x, y);
        context.lineTo(x + 8, y + 5);
        context.stroke();
    }

    function drawPerson(colors, x, y) {
        context.fillStyle = colors.accent;
        context.beginPath();
        context.arc(x, y - 8, 9, 0, Math.PI * 2);
        context.fill();
        context.beginPath();
        context.arc(x, y + 20, 17, Math.PI, 0);
        context.fill();
    }

    function draw() {
        const story = format.value === 'story';
        const colors = themes[theme.value];
        const ingredients = showIngredients.checked ? recipe.ingredients : [];
        // Changing the size also clears the canvas and resets the context state.
        canvas.width = 1080;
        canvas.height = story ? 1920 : 1080;
        const { width, height } = canvas;
        const inner = width - padding * 2;

        const glow = context.createRadialGradient(width, 0, 0, width, 0, width);
        glow.addColorStop(0, colors.glow);
        glow.addColorStop(1, colors.background);
        context.fillStyle = glow;
        context.fillRect(0, 0, width, height);
        context.textBaseline = 'top';

        let y = padding;
        const photoHeight = story ? 820 : ingredients.length ? 300 : 440;
        context.save();
        context.beginPath();
        context.roundRect(padding, y, inner, photoHeight, 40);
        context.clip();
        if (photo) drawCover(photo, padding, y, inner, photoHeight);
        else drawPlate(colors, padding, y, inner, photoHeight);
        context.restore();
        y += photoHeight + 40;

        setFont(600, 28);
        const label = wrap(recipe.category.toUpperCase(), inner - 48, 1)[0];
        context.fillStyle = colors.accent;
        context.beginPath();
        context.roundRect(padding, y, context.measureText(label).width + 48, 48, 24);
        context.fill();
        context.fillStyle = colors.onAccent;
        context.fillText(label, padding + 24, y + 10);
        y += 48 + 28;

        // The title starts big and gets smaller until it fits into the allowed number of lines.
        const titleLines = story ? 3 : 2;
        let size = story ? 88 : 72;
        setFont(700, size);
        while (size > 44 && wrap(recipe.title, inner).length > titleLines) setFont(700, size -= 4);
        context.fillStyle = colors.text;
        for (const line of wrap(recipe.title, inner, titleLines)) {
            context.fillText(line, padding, y);
            y += size * 1.15;
        }
        y += 20;

        setFont(500, 32);
        context.fillStyle = colors.text;
        drawClock(colors, padding + 17, y + 18);
        const time = `${recipe.minutes} min`;
        context.fillText(time, padding + 50, y + 2);
        const servingsX = padding + 50 + context.measureText(time).width + 56;
        drawPerson(colors, servingsX + 17, y + 14);
        context.fillStyle = colors.text;
        context.fillText(`${recipe.servings} ${recipe.servings === 1 ? 'serving' : 'servings'}`, servingsX + 50, y + 2);
        y += 36 + 36;

        const footerY = height - padding - 30;
        context.strokeStyle = colors.muted;
        context.lineWidth = 2;
        context.beginPath();
        context.moveTo(padding, footerY - 24);
        context.lineTo(width - padding, footerY - 24);
        context.stroke();
        setFont(700, 30);
        context.fillStyle = colors.text;
        context.fillText('Recipe Manager', padding, footerY);
        setFont(400, 26);
        context.fillStyle = colors.muted;
        context.textAlign = 'right';
        context.fillText(location.host, width - padding, footerY + 3);
        context.textAlign = 'left';

        if (!ingredients.length) return;
        setFont(600, 28);
        context.fillStyle = colors.muted;
        context.fillText('Ingredients', padding, y);
        y += 44;
        const rowHeight = 44;
        const rows = Math.floor((footerY - 44 - y) / rowHeight);
        if (rows < 1) return;
        const columnWidth = (inner - 48) / 2;
        const slots = rows * 2;
        const shown = ingredients.length > slots ? ingredients.slice(0, slots - 1) : ingredients;
        const items = shown.map(item => ({ name: item.name, amount: item.amount }));
        if (shown.length < ingredients.length) items.push({ name: `+${ingredients.length - shown.length} more`, amount: '' });
        setFont(400, 30);
        items.forEach((item, index) => {
            const x = padding + (index % 2) * (columnWidth + 48) + 28;
            const rowY = y + Math.floor(index / 2) * rowHeight;
            context.fillStyle = colors.accent;
            context.beginPath();
            context.arc(x - 21, rowY + 17, 7, 0, Math.PI * 2);
            context.fill();
            // When the row is too long, the name is shortened and the amount stays readable.
            const amount = item.amount ? ` · ${wrap(item.amount, columnWidth / 2, 1)[0]}` : '';
            const name = wrap(item.name, columnWidth - 28 - context.measureText(amount).width, 1)[0];
            context.fillStyle = colors.text;
            context.fillText(name, x, rowY);
            context.fillStyle = colors.muted;
            context.fillText(amount, x + context.measureText(name).width, rowY);
        });
    }

    download.addEventListener('click', () => {
        canvas.toBlob(blob => {
            const link = document.createElement('a');
            link.href = URL.createObjectURL(blob);
            link.download = `recipe-${recipe.id}-${format.value}.png`;
            link.click();
            setTimeout(() => URL.revokeObjectURL(link.href), 1000);
        }, 'image/png');
    });
    document.getElementById('recipe-card-options').addEventListener('change', draw);

    Promise.all([loadPhoto(), document.fonts.ready]).then(([image]) => {
        photo = image;
        draw();
        download.disabled = false;
    });
})();
