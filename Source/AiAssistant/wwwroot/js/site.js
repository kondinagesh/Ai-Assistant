// Mobile menu toggle
document.addEventListener('DOMContentLoaded', function () {
    const menuButton = document.createElement('button');
    menuButton.className = 'mobile-menu-button';
    menuButton.innerHTML = '<i class="fas fa-bars"></i>';
    
    // Insert the button into the menu container
    document.getElementById('menu-container').appendChild(menuButton);

    menuButton.addEventListener('click', function () {
        document.querySelector('.sidebar').classList.toggle('show');
    });
});