#!/usr/bin/env python3
"""Reject incomplete, substituted or unreceipted native producer inputs."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location('producer_contract', Path(__file__).with_name('game-event-producer-contract.py'))
contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(contract)


class ProducerEvidenceTests(unittest.TestCase):
    def prepare(self, root):
        rows = []
        index = 1
        for label, (count, players, bots) in contract.CASES.items():
            directory = root / 'producer-records' / label
            directory.mkdir(parents=True)
            for _ in range(count):
                identity = str(uuid.UUID(int=index, version=4))
                index += 1
                filename = identity + '.json'
                raw = json.dumps(dict(spoolVersion=1, recordId=identity,
                                      event=dict(type='round.ended', occurredAtUtc='2026-11-16T09:00:00Z',
                                                 map='de_dust2', players=players, bots=bots))).encode()
                (directory / filename).write_bytes(raw)
                rows.append([label, filename, str(players), str(bots), str(len(raw)), hashlib.sha256(raw).hexdigest()])
        self.write_rows(root, rows)
        return rows

    @staticmethod
    def write_rows(root, rows):
        (root / 'producer-records.tsv').write_text(''.join('\t'.join(row) + '\n' for row in rows))

    def test_complete_native_case_matrix(self):
        with tempfile.TemporaryDirectory(prefix='producer-evidence-test-') as folder:
            root = Path(folder)
            self.prepare(root)
            self.assertEqual(len(contract.read_records(root)), 10)

    def test_record_tampering_and_unreceipted_file_fail(self):
        for corruption in ('bytes', 'extra-file', 'missing-case', 'duplicate-id', 'roster', 'path'):
            with self.subTest(corruption=corruption), tempfile.TemporaryDirectory(prefix='producer-rejection-test-') as folder:
                root = Path(folder)
                rows = self.prepare(root)
                source = root / 'producer-records' / rows[0][0] / rows[0][1]
                if corruption == 'bytes':
                    source.write_bytes(source.read_bytes() + b' ')
                elif corruption == 'extra-file':
                    source.with_name('unreceipted.tmp').write_bytes(b'incomplete')
                elif corruption == 'missing-case':
                    source.unlink()
                    rows.pop(0)
                elif corruption == 'duplicate-id':
                    rows.append(rows[0])
                elif corruption == 'roster':
                    value = json.loads(source.read_bytes())
                    value['event']['bots'] = 0
                    raw = json.dumps(value).encode()
                    source.write_bytes(raw)
                    rows[0][4:] = [str(len(raw)), hashlib.sha256(raw).hexdigest()]
                else:
                    rows[0][1] = '../escape.json'
                self.write_rows(root, rows)
                with self.assertRaises(ValueError):
                    contract.read_records(root)


if __name__ == '__main__':
    unittest.main(verbosity=2)
