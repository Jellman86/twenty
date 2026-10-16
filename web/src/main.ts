import { mount } from 'svelte';
import App from './App.svelte';
import './style.css';

// An expired or revoked session surfaces as a 401 from any API call; return to the sign-in screen.
const send = window.fetch.bind(window);
window.fetch = async (input, init) => {
  const response = await send(input, init);
  const url = typeof input === 'string' ? input : input instanceof URL ? input.pathname : input.url;
  if (response.status === 401 && url.startsWith('/api/') && !url.startsWith('/api/auth/'))
    window.dispatchEvent(new Event('twenty:unauthorized'));
  return response;
};

mount(App, { target: document.getElementById('app')! });
