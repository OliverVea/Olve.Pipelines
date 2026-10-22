import type { ReactiveController, ReactiveControllerHost } from 'lit';

/** Calls `reload` every `ms` while the tab is visible, and once on return to it. */
export class PollController implements ReactiveController {
  private _timer?: ReturnType<typeof setInterval>;
  private readonly reload: () => void;
  private readonly ms: number;

  constructor(host: ReactiveControllerHost, reload: () => void, ms = 10_000) {
    this.reload = reload;
    this.ms = ms;
    host.addController(this);
  }

  hostConnected() {
    document.addEventListener('visibilitychange', this._onVisibility);
    this._start();
  }

  hostDisconnected() {
    document.removeEventListener('visibilitychange', this._onVisibility);
    this._stop();
  }

  private _onVisibility = () => {
    if (document.hidden) return this._stop();
    this.reload();
    this._start();
  };

  private _start() {
    this._stop();
    if (!document.hidden) this._timer = setInterval(this.reload, this.ms);
  }

  private _stop() {
    if (this._timer) clearInterval(this._timer);
    this._timer = undefined;
  }
}
