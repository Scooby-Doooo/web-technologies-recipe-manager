(($) => {
    const select = document.getElementById('CategoryId');
    if (!select) return;
    if (!$ || !$.fn.select2) {
        document.getElementById('category-search-status').hidden = false;
        return;
    }

    // Unobtrusive validation registers on DOM-ready before this callback.
    $(() => {
        const field = $(select);
        field.select2({
            width: '100%',
            placeholder: 'Search for a category',
            minimumInputLength: 3,
            maximumInputLength: 100,
            allowClear: true,
            ajax: {
                url: select.dataset.searchUrl,
                dataType: 'json',
                delay: 300,
                data: params => ({ term: params.term.trim(), page: params.page || 1 }),
                transport: (params, success, failure) => {
                    const currentQuery = () => {
                        const search = document.querySelector('.select2-container--open .select2-search__field');
                        return !search || search.value.trim() === params.data.term;
                    };
                    const request = $.ajax(params);
                    request.done(data => { if (currentQuery()) success(data); });
                    request.fail((_, reason) => {
                        if (reason !== 'abort' && currentQuery()) failure();
                    });
                    // Select2 otherwise treats every status-0 network failure as cancellation.
                    return { abort: () => request.abort() };
                }
            }
        });
        document.getElementById('category-search-status').hidden = true;

        const selection = field.next('.select2').find('.select2-selection');
        selection.attr({
            'aria-labelledby': 'category-label select2-CategoryId-container',
            'aria-describedby': 'categoryHelp categoryValidation',
            'aria-invalid': String(field.hasClass('input-validation-error'))
        });
        const validator = field.closest('form').data('validator');
        if (validator) {
            // Select2 hides the original select, which still carries MVC validation rules.
            validator.settings.ignore = ':hidden:not(.recipe-category-autocomplete)';
            field.on('change', () => selection.attr('aria-invalid', String(!field.valid())));
            field.closest('form').on('invalid-form', () => {
                selection.attr('aria-invalid', String(Boolean(validator.errorMap.CategoryId)));
                if (validator.errorList[0]?.element === select) selection.trigger('focus');
            });
        }
        field.on('select2:open', () => {
            const search = document.querySelector('.select2-container--open .select2-search__field');
            search.setAttribute('aria-label', 'Search categories');
            search.setAttribute('aria-describedby', 'categoryHelp');
            search.focus();
        });
    });
})(window.jQuery);
