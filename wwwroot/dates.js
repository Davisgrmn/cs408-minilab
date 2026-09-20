for (const time of document.querySelectorAll('time[datetime]')) {
  time.textContent = new Date(time.dateTime).toLocaleString(undefined, {
    month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
    timeZoneName: 'short',
  });
}

document.querySelector('form')?.addEventListener('submit', () => {
  const button = document.querySelector('button');
  button.disabled = true;
  button.textContent = 'Loading…';
});
