// Theme toggling
document.querySelector('[title="Toggle theme"]').addEventListener('click', function () {
    document.body.classList.toggle('dark-theme');
    const icon = this.querySelector('i');
    if (icon.classList.contains('fa-moon')) {
        icon.classList.replace('fa-moon', 'fa-sun');
    } else {
        icon.classList.replace('fa-sun', 'fa-moon');
    }
});

// Mobile menu toggle
document.addEventListener('DOMContentLoaded', function () {
    const menuButton = document.createElement('button');
    menuButton.className = 'mobile-menu-button';
    menuButton.innerHTML = '<i class="fas fa-bars"></i>';
    document.querySelector('.top-header > div:first-child').appendChild(menuButton);

    menuButton.addEventListener('click', function () {
        document.querySelector('.sidebar').classList.toggle('show');
    });
});

// Share functionality
document.querySelector('[title="Share"]').addEventListener('click', function () {
    if (navigator.share) {
        navigator.share({
            title: 'Blazor OpenAI',
            text: 'Check out this awesome AI-powered application!',
            url: window.location.href
        });
    }
});