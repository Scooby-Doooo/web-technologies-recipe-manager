// Recipe photo field: checks the chosen file with the File API, shows a preview and shrinks large
// photos on a canvas before upload. The server still validates the file on its own.
(() => {
    const input = document.getElementById('imageFile');
    if (!input) return;
    const preview = document.getElementById('image-preview');
    const previewImage = preview.querySelector('img');
    const info = document.getElementById('image-info');
    const error = document.getElementById('image-error');
    const submit = input.form.querySelector('[type="submit"]');
    const types = ['image/jpeg', 'image/png', 'image/webp'];
    const maximumSize = 5 * 1024 * 1024;
    const compressFrom = 1024 * 1024;
    const maximumSide = 1600;
    let previewUrl = null;

    const size = bytes => bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`;

    function reset(message) {
        if (previewUrl) URL.revokeObjectURL(previewUrl);
        previewUrl = null;
        preview.hidden = true;
        error.textContent = message || '';
        error.hidden = !message;
    }

    function encode(canvas, type) {
        return new Promise(resolve => canvas.toBlob(resolve, type, 0.85));
    }

    async function shrink(file, image) {
        const scale = Math.min(1, maximumSide / Math.max(image.naturalWidth, image.naturalHeight));
        const canvas = document.createElement('canvas');
        canvas.width = Math.round(image.naturalWidth * scale);
        canvas.height = Math.round(image.naturalHeight * scale);
        const context = canvas.getContext('2d');
        context.drawImage(image, 0, 0, canvas.width, canvas.height);
        let blob = await encode(canvas, 'image/webp');
        if (blob?.type !== 'image/webp') {
            // Safari cannot encode WEBP; JPEG has no transparency, so paint a white background first.
            context.globalCompositeOperation = 'destination-over';
            context.fillStyle = '#fff';
            context.fillRect(0, 0, canvas.width, canvas.height);
            blob = await encode(canvas, 'image/jpeg');
        }
        if (!blob || blob.size >= file.size) return { file, width: image.naturalWidth, height: image.naturalHeight };
        const extension = blob.type === 'image/webp' ? '.webp' : '.jpg';
        const name = file.name.replace(/\.[^.]*$/, '') + extension;
        return { file: new File([blob], name, { type: blob.type, lastModified: file.lastModified }), width: canvas.width, height: canvas.height };
    }

    async function choose(file) {
        if (!types.includes(file.type)) return reset('Choose a JPG, JPEG, PNG or WEBP image.');
        // An <img> decodes the file the same way the page will show it, EXIF rotation included.
        const image = new Image();
        image.src = URL.createObjectURL(file);
        let result;
        try {
            await image.decode();
            const needsShrink = file.size > compressFrom || Math.max(image.naturalWidth, image.naturalHeight) > maximumSide;
            result = needsShrink ? await shrink(file, image) : { file, width: image.naturalWidth, height: image.naturalHeight };
        } catch {
            return reset('This file cannot be read as an image.');
        } finally {
            URL.revokeObjectURL(image.src);
        }
        if (result.file.size > maximumSize) return reset('The image is still larger than 5 MB after compression.');
        if (result.file !== file) {
            // Put the compressed file into the input, so the normal form post sends it.
            const transfer = new DataTransfer();
            transfer.items.add(result.file);
            input.files = transfer.files;
        }
        reset();
        previewUrl = URL.createObjectURL(result.file);
        previewImage.src = previewUrl;
        info.textContent = `${result.file.name}: ${result.width} × ${result.height} px, ${size(result.file.size)}`
            + (result.file !== file ? ` (compressed from ${size(file.size)})` : '');
        preview.hidden = false;
    }

    input.addEventListener('change', async () => {
        const file = input.files[0];
        reset();
        if (!file) return;
        submit.disabled = true;
        try {
            await choose(file);
            if (!error.hidden) input.value = '';
        } finally {
            submit.disabled = false;
        }
    });
})();
