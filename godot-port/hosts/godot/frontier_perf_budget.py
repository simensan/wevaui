"""Evaluate measured Frontier UI timings against an explicit environment profile."""
import argparse
import json
import math
from pathlib import Path


def evaluate(report, budget):
    """Fail closed on missing evidence; every run must meet each p95 limit."""
    errors = []
    checks = []
    if budget.get('schema') != 1:
        raise ValueError('Unsupported budget schema')
    limits = budget.get('limits_ms', {})
    renderers = budget.get('renderers', [])
    if not limits or not renderers or len(set(renderers)) != len(renderers):
        raise ValueError('Budget needs limits and distinct renderers')
    for case, metrics in limits.items():
        if not metrics:
            raise ValueError('Empty workload budget: ' + case)
        for metric, limit in metrics.items():
            if metric not in ('api_cpu', 'changed_api_cpu', 'core_cpu', 'changed_core_cpu', 'whole_frame', 'changed_whole_frame') or isinstance(limit, bool) or not isinstance(limit, (float, int)) or not math.isfinite(limit) or limit <= 0:
                raise ValueError('Invalid p95 time limit: ' + case + '/' + metric)
    for key in ('minimum_runs', 'minimum_frames', 'minimum_warmups'):
        if type(budget.get(key)) is not int or budget[key] < 1:
            raise ValueError('Budget needs positive integer ' + key)
    scope = budget.get('scope', {})
    if set(scope) != {'resolution', 'world', 'automatic', 'adapter'}:
        raise ValueError('Budget must specify resolution, world, automatic and adapter scope')
    if (not isinstance(scope['resolution'], list) or len(scope['resolution']) != 2 or
            any(type(n) is not int or n <= 0 for n in scope['resolution']) or
            scope['world'] not in ('static', '3d') or type(scope['automatic']) is not bool or
            not isinstance(scope['adapter'], str) or not scope['adapter']):
        raise ValueError('Invalid measurement scope')
    for key in ('resolution', 'world', 'automatic'):
        if report.get(key) != scope[key]:
            errors.append('Scope mismatch: ' + key)
    if report.get('functional_passed', report.get('passed', False)) is not True:
        errors.append('Functional benchmark did not pass')
    for key, minimum in [('frames', 'minimum_frames'), ('warmups', 'minimum_warmups')]:
        if type(report.get(key)) is not int or report[key] < budget[minimum]:
            errors.append('Insufficient ' + key)
    runs = report.get('runs', [])
    if any(run.get('renderer') not in renderers for run in runs):
        errors.append('Report contains renderers outside the budget scope')
    for renderer in renderers:
        selected = [run for run in runs if run.get('renderer') == renderer]
        identities = [run.get('index') for run in selected]
        if len(selected) < budget['minimum_runs']:
            errors.append('Insufficient runs: ' + renderer)
        if any(type(i) is not int for i in identities) or len(set(identities)) != len(identities):
            errors.append('Invalid or duplicate run indices: ' + renderer)
        for run in selected:
            label = renderer + '/' + str(run.get('index'))
            result = run.get('result', {})
            if result.get('adapter') != scope['adapter']:
                errors.append('Adapter mismatch: ' + label)
            if result.get('passed') is not True or result.get('debug_build') is not False:
                errors.append('Not a passing release run: ' + label)
            if result.get('viewport') != scope['resolution'] or result.get('world') != scope['world'] or result.get('auto_update') != scope['automatic']:
                errors.append('Run scope mismatch: ' + label)
            if result.get('renderer') != renderer:
                errors.append('Renderer mismatch: ' + label)
            cases = result.get('results', [])
            names = [case.get('workload') for case in cases]
            if len(names) != len(set(names)):
                errors.append('Duplicate workloads: ' + label)
            by_name = {case.get('workload'): case for case in cases}
            for case, metrics in limits.items():
                data = by_name.get(case, {})
                for key, minimum in [('frames', 'minimum_frames'), ('warmups', 'minimum_warmups')]:
                    if type(data.get(key)) is not int or data[key] < budget[minimum]:
                        errors.append('Insufficient workload ' + key + ': ' + label + '/' + case)
                    elif data[key] != report.get(key):
                        errors.append('Workload/report length mismatch: ' + label + '/' + case + '/' + key)
                for metric, limit in metrics.items():
                    samples = data.get(metric, {})
                    value = samples.get('p95_ms')
                    valid = (not isinstance(value, bool) and isinstance(value, (float, int)) and math.isfinite(value) and value >= 0)
                    # A missing/empty changed-frame population is not zero cost.
                    minimum_samples = max(10, budget['minimum_frames'] // 60) if metric.startswith('changed_') else budget['minimum_frames']
                    valid = valid and type(samples.get('samples')) is int and samples['samples'] >= minimum_samples
                    passed = valid and value <= limit
                    count = samples.get('samples')
                    count = count if type(count) is int and count >= 0 else None
                    checks.append({'run': label, 'workload': case, 'metric': metric,
                                   'samples': count, 'minimum_samples': minimum_samples,
                                   'p95_rank': math.ceil(count * .95) if count else None,
                                   'p95_ms': value if valid else None, 'limit_ms': limit, 'passed': passed})
                    if not passed:
                        errors.append(('Missing/invalid timing: ' if not valid else 'Budget exceeded: ') + label + '/' + case + '/' + metric)
    return {'profile': budget.get('name', 'unnamed'), 'passed': not errors, 'errors': errors,
            'checks': checks, 'policy': 'Every measured run must meet every p95 time limit; no averaging away failures.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--budget', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(json.loads(args.report.read_text(encoding='utf-8')),
                      json.loads(args.budget.read_text(encoding='utf-8')))
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + '\n', encoding='utf-8')
    print(('PASS' if result['passed'] else 'FAIL') + ': ' + result['profile'])
    for error in result['errors']:
        print(error)
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
