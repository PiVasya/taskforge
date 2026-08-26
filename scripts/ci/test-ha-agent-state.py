#!/usr/bin/env python3
from types import SimpleNamespace
import threading
import sys
sys.dont_write_bytecode = True
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'deploy' / 'cluster'))
from agent import Controller  # noqa: E402


def ok():
    return SimpleNamespace(returncode=0, stdout='', stderr='')


def test_lite_profile_never_assigns_heavy_services():
    c = object.__new__(Controller)
    c.node = SimpleNamespace(
        app_profile='lite',
        app_exclude_services=('browser-api', 'image-analyzer'),
        assist_exclude_services=('support-bot', 'telegram-quiz-bot', 'rating-worker'),
    )
    c.application_services = lambda: [
        'gateway', 'front', 'identity-api', 'browser-api', 'image-analyzer',
        'support-bot', 'telegram-quiz-bot', 'rating-worker', 'tasks-api'
    ]
    assigned = c.assigned_services()
    assert 'browser-api' not in assigned
    assert 'image-analyzer' not in assigned
    assert 'gateway' in assigned and 'tasks-api' in assigned
    assist = c.assist_services()
    assert 'support-bot' not in assist
    assert 'telegram-quiz-bot' not in assist
    assert 'rating-worker' not in assist


def test_standby_prepare_is_stopped_no_deps_and_local_first():
    c = object.__new__(Controller)
    c.deploy_lock = threading.RLock()
    c.app_active = False
    c.local_role = 'standby'
    calls = []
    c.compose = lambda *args, **kwargs: calls.append((args, kwargs)) or ok()
    c.prepare_standby_containers(['gateway', 'front'])
    assert len(calls) == 1
    args, kwargs = calls[0]
    assert args[:7] == ('up', '--no-start', '--no-deps', '--pull', 'never', 'gateway', 'front')
    assert kwargs.get('check') is False


def test_slow_initial_pull_does_not_override_promotion():
    c = object.__new__(Controller)
    c.deploy_lock = threading.RLock()
    c.app_active = False
    c.local_role = 'standby'
    calls = []

    def compose(*args, **kwargs):
        calls.append(args)
        if args[0] == 'up':
            return SimpleNamespace(returncode=1, stdout='', stderr='image missing')
        if args[0] == 'pull':
            c.local_role = 'primary'  # failover won while registry work was in flight
            return ok()
        raise AssertionError(args)

    c.compose = compose
    c.prepare_standby_containers(['gateway'])
    assert calls == [
        ('up', '--no-start', '--no-deps', '--pull', 'never', 'gateway'),
        ('pull', 'gateway'),
    ]


def test_hot_activation_never_pulls_registry():
    c = object.__new__(Controller)
    c.deploy_lock = threading.RLock()
    c.app_active = False
    c.app_mode = 'warm-standby'
    c.last_app_reconcile = 0.0
    calls = []
    c.compose = lambda *args, **kwargs: calls.append(args) or ok()
    c._activate_prepared_service_set(['gateway', 'front'], 'primary')
    assert calls[0] == ('up', '--no-start', '--no-deps', '--pull', 'never', 'gateway', 'front')
    assert calls[1] == ('start', 'gateway', 'front')
    assert c.app_active is True and c.app_mode == 'primary'


def test_promotion_wins_over_late_standby_prepare():
    c = object.__new__(Controller)
    c.deploy_lock = threading.RLock()
    c.app_active = True
    c.local_role = 'primary'
    c.compose = lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError('standby prepare must not touch active apps'))
    c.prepare_standby_containers(['gateway'])


def test_empty_profile_does_not_touch_compose():
    c = object.__new__(Controller)
    c.deploy_lock = threading.RLock()
    c.app_active = False
    c.local_role = 'standby'
    c.compose = lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError('compose must not run'))
    c.prepare_standby_containers([])


def test_lite_prunes_stale_excluded_container_before_watchtower_updates_it():
    c = object.__new__(Controller)
    c.deploy_lock = threading.RLock()
    c.node = SimpleNamespace(app_profile='lite', app_exclude_services=('browser-api', 'image-analyzer'))
    c.application_services = lambda: ['gateway', 'browser-api', 'image-analyzer']
    c.assigned_services = lambda: ['gateway']
    calls = []

    def compose(*args, **kwargs):
        calls.append(args)
        if args[:3] == ('ps', '-a', '-q'):
            return SimpleNamespace(returncode=0, stdout='cid\n' if args[3] == 'browser-api' else '', stderr='')
        if args[:3] == ('rm', '-s', '-f'):
            return ok()
        return ok()

    c.compose = compose
    stale = c.unassigned_application_containers()
    assert stale == ['browser-api']
    c.prune_unassigned_application_containers(stale)
    assert ('rm', '-s', '-f', 'browser-api') in calls
    assert ('rm', '-s', '-f', 'image-analyzer') not in calls


def test_watchtower_start_failure_is_not_ha_readiness_failure():
    c = object.__new__(Controller)
    c.deploy_lock = threading.RLock()
    c.watchtower_active = False
    c.watchtower_error = ''
    c.last_pull_status = 'idle'
    c.last_pull_error = ''
    c.app_active = True
    c.services = lambda: ['watchtower']
    c.watchtower_running = lambda: False
    c.watchtower_container_id = lambda: 'watchtower-id'
    c.compose = lambda *args, **kwargs: SimpleNamespace(returncode=1, stdout='', stderr='synthetic updater failure')
    c._record_watchtower_failure = lambda message: setattr(c, 'watchtower_error', message)
    c._record_watchtower_recovered = lambda: None
    assert c.reconcile_watchtower(True) is False
    assert 'synthetic updater failure' in c.watchtower_error


if __name__ == '__main__':
    test_lite_profile_never_assigns_heavy_services()
    print('PASS: lite profile excludes browser/image and assist singletons')
    test_standby_prepare_is_stopped_no_deps_and_local_first()
    print('PASS: warm preparation is stopped/no-deps and tries cached images first')
    test_slow_initial_pull_does_not_override_promotion()
    print('PASS: long initial registry pull does not block/override a promotion')
    test_hot_activation_never_pulls_registry()
    print('PASS: hot activation reconciles cached images with --pull never then starts')
    test_promotion_wins_over_late_standby_prepare()
    print('PASS: failover activation wins over a late standby preparation')
    test_empty_profile_does_not_touch_compose()
    print('PASS: empty profile is a no-op')
    test_lite_prunes_stale_excluded_container_before_watchtower_updates_it()
    print('PASS: lite standby removes stale excluded containers before Watchtower can update them')
    test_watchtower_start_failure_is_not_ha_readiness_failure()
    print('PASS: updater start failure is non-critical for HA/application readiness')
    print('All v40 Node Agent tests passed.')
