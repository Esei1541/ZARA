const REQUEST_PATTERN = /^#v=1&request=([0-9a-f]{64})$/;

export function parseRecoveryFragment(fragment) {
  if (!fragment || fragment === '#') return { kind: 'missing' };

  const match = REQUEST_PATTERN.exec(fragment);
  if (!match) return { kind: 'invalid' };

  return { kind: 'valid', request: match[1] };
}

export function createRecoveryBytes(request) {
  if (typeof request !== 'string' || !/^[0-9a-f]{64}$/.test(request)) {
    throw new TypeError('Invalid recovery request');
  }

  return new TextEncoder().encode(JSON.stringify({
    format: 'zara-recovery',
    version: 1,
    request,
  }));
}

if (typeof document !== 'undefined') {
  const downloadButton = document.getElementById('download');
  const status = document.getElementById('download-status');
  const parsed = parseRecoveryFragment(window.location.hash);

  if (parsed.kind !== 'valid') {
    downloadButton.disabled = true;
    status.textContent = parsed.kind === 'missing'
      ? '잠금 해제 정보가 없습니다. 잠긴 PC의 잠금 해제 안내 창에서 QR 코드를 스캔해주세요.'
      : '잠금 해제 정보를 읽을 수 없습니다. 잠긴 PC에 표시된 QR 코드를 다시 스캔해주세요.';
    status.hidden = false;
  } else {
    downloadButton.addEventListener('click', () => {
      try {
        const bytes = createRecoveryBytes(parsed.request);
        const blob = new Blob([bytes], { type: 'application/octet-stream' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = 'zara-recovery.key';
        document.body.appendChild(link);
        try {
          link.click();
        } finally {
          link.remove();
          window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
        }
        status.textContent = '파일을 저장한 뒤 아래 연결 방법을 따라 진행해주세요.';
      } catch {
        status.textContent = '복구 파일을 준비하지 못했습니다. 페이지를 새로고침한 뒤 다시 시도해주세요.';
      }
      status.hidden = false;
    });
  }
}
