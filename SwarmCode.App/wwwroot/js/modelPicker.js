export function initialize(element) {
    element.querySelector('input').addEventListener('keydown', event => {
        if (['ArrowDown', 'ArrowUp', 'Enter', 'Escape'].includes(event.key)) event.preventDefault();
    });
}
export function scrollActive(element) {
    element.querySelector('.is-active')?.scrollIntoView({block: 'nearest'});
}
