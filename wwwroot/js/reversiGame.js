// ReversiGame.js - JavaScript functions for ReversiGame.razor

window.ReversiChat = window.ReversiChat || {};
window.ReversiChat.scrollToBottom = function (el) {
  try {
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  } catch (e) {
  }
};

window.ReversiChat.wireEnterToSend = function (el, dotNetRef) {
  try {
    if (!el || !dotNetRef) return;
    if (el.__ReversiEnterWired) return;
    el.__ReversiEnterWired = true;
    el.addEventListener('keydown', function (ev) {
      try {
        if (ev.key === 'Enter') {
          ev.preventDefault();
          dotNetRef.invokeMethodAsync('OnChatEnterFromJs');
        }
      } catch (e) {
      }
    });
  } catch (e) {
  }
};

// Voice (speech recognition / TTS) lives in the GameVoice package: _content/GameVoice/gameVoice.js (window.GameVoice).
