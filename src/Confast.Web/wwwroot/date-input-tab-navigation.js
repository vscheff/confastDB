export function skipNativePickerOnTab(container) {
    const input = container.querySelector('input[type="date"]');

    if (!input || input.dataset.tabNavigationConfigured) {
        return;
    }

    input.dataset.tabNavigationConfigured = 'true';
    input.addEventListener('keydown', event => {
        if (event.key !== 'Tab') {
            return;
        }

        event.preventDefault();

        const focusableElements = Array.from(document.querySelectorAll(
            'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'))
            .filter(element => !element.hidden && element.getClientRects().length > 0);
        const currentIndex = focusableElements.indexOf(input);
        const nextIndex = currentIndex + (event.shiftKey ? -1 : 1);

        focusableElements[nextIndex]?.focus();
    });
}
