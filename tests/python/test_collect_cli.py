"""python -m pqax collect: the checks of the command line with their messages and exit code 2, the runs on --local,
--mount, and --workspace against the fake OneLake endpoint, the progress lines, the identities that need nothing more
than the standard library, and the secrets that are never echoed (the interface of the Python collector, which follows
CollectOptions.cs and CollectCommand.cs of the C# command).

The command runs in the process, through pqax.__main__.main, and in a process of its own where a library must look
missing. The runs that collect need pyarrow, the extra pqax[collect]: the command reads the checkpoints with it.
"""
from __future__ import annotations

import datetime
import os
import re
import subprocess
import sys
import time

import pytest

import pqax
from collect_kit import (FIXTURE, HAS_AZURE_IDENTITY, HAS_PYARROW, ORDERS, SALES, TABLES, FakeDfs, copy_tree, jwt,
                         rows, run_cli, sample)
from pqax_kit import ROOT

needs_pyarrow = pytest.mark.skipif(not HAS_PYARROW, reason="collecting needs pyarrow, pqax[collect]")
LAKEHOUSE = str(FIXTURE)
WORKSPACE = "Test Workspace"
TOKEN = "fake-access-token-for-the-tests-7Q2"
TENANT = "00000000-1111-2222-3333-444444444444"
CLIENT_ID = "55555555-6666-7777-8888-999999999999"
SECRET = "Sup3r-S3cret~Value.42"
PROGRESS = re.compile(r"^  table (\d+) of (\d+): (\d+) footers, (\d+) issues; (\d+) requests so far$")
FINAL = re.compile(r"^(.+): (\d+) tables, (\d+) footers read, (\d+) issues, (\d+) requests, (\d+) bytes read; valid$")


def lines(text):
    return [line.rstrip("\r") for line in text.splitlines() if line.strip()]


def refused(capsys, arguments, message=None, output=None):
    """Runs a command line that must be refused: exit code 2, the message after "pqax collect: ", and no file."""
    code, out, err = run_cli(["collect", *arguments], capsys)
    assert code == 2, out + err
    if message is not None:
        assert "pqax collect: " + message in lines(err), err
    if output is not None:
        assert not os.path.exists(output)
    return out, err


# ---- the command line ----

def workspace(output, *identity):
    return ["--workspace", WORKSPACE, "--item", "Item.Lakehouse", "--output", str(output), *identity]


CHECKS = [
    (lambda out: [], "give one of --local, --mount, --workspace, or --adls."),
    (lambda out: ["--output", out], "give one of --local, --mount, --workspace, or --adls."),
    (lambda out: ["--local", LAKEHOUSE, "--workspace", WORKSPACE, "--item", "I.Lakehouse", "--output", out],
     "give one of --local, --mount, --workspace, or --adls."),
    (lambda out: ["--local", LAKEHOUSE, "--mount", LAKEHOUSE, "--output", out], "give one of --local, --mount, --workspace, or --adls."),
    (lambda out: ["--workspace", WORKSPACE, "--output", out], "--workspace needs at least one --item."),
    (lambda out: ["--local", LAKEHOUSE], "--output is missing."),
    (lambda out: ["--local", LAKEHOUSE, "--output"], "--output needs a value."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--depth", "deep"],
     "unknown depth 'deep'; use summary, files, rowGroups, or columnChunks."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--level", "3"], "--level is 1 or 2."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--level", "0"], "--level is 1 or 2."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--max-footers", "-1"], "--max-footers needs a number."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--max-commits", "many"], "--max-commits needs a number."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--parallelism", "0"], "--parallelism needs a number of at least 1."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--parallelism", "x"], "--parallelism needs a number of at least 1."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--no-such-option"], "unknown option '--no-such-option'."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--azure-cli"],
     "--tenant, --client-id, and the identity options apply to --workspace and --adls only."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--tenant", TENANT],
     "--tenant, --client-id, and the identity options apply to --workspace and --adls only."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--access-token-env", "PQAX_TOKEN"],
     "--tenant, --client-id, and the identity options apply to --workspace and --adls only."),
    (lambda out: workspace(out, "--device-code", "--browser"), "--device-code and --browser are two identities; give one."),
    (lambda out: workspace(out, "--azure-cli", "--managed-identity"), "--azure-cli and --managed-identity are two identities; give one."),
    (lambda out: workspace(out, "--access-token-env", "PQAX_TOKEN", "--env-credential"),
     "--access-token-env and --env-credential are two identities; give one."),
    (lambda out: workspace(out, "--client-secret-env", "PQAX_SECRET"), "--client-secret-env needs --client-id and --tenant."),
    (lambda out: workspace(out, "--client-certificate", "cert.pfx", "--tenant", TENANT),
     "--client-certificate needs --client-id and --tenant."),
    (lambda out: workspace(out, "--managed-identity", "--tenant", TENANT),
     "--tenant does not apply to --managed-identity: the identity belongs to one tenant."),
    (lambda out: workspace(out, "--env-credential", "--tenant", TENANT),
     "--env-credential takes the tenant and the client id from AZURE_TENANT_ID and AZURE_CLIENT_ID; leave out --tenant and --client-id."),
    (lambda out: workspace(out, "--access-token-env", "PQAX_TOKEN", "--client-id", CLIENT_ID),
     "--tenant and --client-id do not apply to --access-token-env."),
    (lambda out: workspace(out, "--client-id", CLIENT_ID),
     "--client-id goes with --client-secret-env, --client-certificate, or --managed-identity."),
    (lambda out: workspace(out, "--client-id", CLIENT_ID, "--azure-cli"),
     "--client-id goes with --client-secret-env, --client-certificate, or --managed-identity."),
    (lambda out: workspace(out, "--client-certificate-password-env", "PQAX_PFX"),
     "--client-certificate-password-env goes with --client-certificate."),
    (lambda out: workspace(out, "--access-token-env", "1PQAX_TOKEN"),
     "--access-token-env takes the name of an environment variable, made of letters, digits, and underscores, such as PQAX_SECRET; never the secret itself."),
    (lambda out: workspace(out, "--client-id", CLIENT_ID, "--tenant", TENANT, "--client-secret-env", "PQAX-SECRET"),
     "--client-secret-env takes the name of an environment variable, made of letters, digits, and underscores, such as PQAX_SECRET; never the secret itself."),
]


@pytest.mark.parametrize("index", range(len(CHECKS)))
def test_a_wrong_command_line_exits_with_2_and_the_message_of_the_check(capsys, tmp_path, index):
    arguments, message = CHECKS[index]
    output = str(tmp_path / "out.pqax")
    refused(capsys, arguments(output), message, output)


@pytest.mark.parametrize("arguments, message", [
    (lambda out: ["--mount", LAKEHOUSE, "--item", "A.Lakehouse", "--item", "B.Lakehouse", "--output", out],
     "--mount reads one item; give at most one --item, which names it."),
    (lambda out: ["--local", LAKEHOUSE, "--output", out, "--endpoint", "http://127.0.0.1:9"],
     "--endpoint applies to --workspace only."),
    (lambda out: ["--mount", LAKEHOUSE, "--output", out, "--endpoint", "http://127.0.0.1:9"],
     "--endpoint applies to --workspace only."),
    (lambda out: ["--mount", LAKEHOUSE, "--output", out, "--browser"],
     "--tenant, --client-id, and the identity options apply to --workspace and --adls only."),
], ids=["mount with two items", "endpoint with local", "endpoint with mount", "identity with mount"])
def test_the_options_of_python_are_checked_too(capsys, tmp_path, arguments, message):
    output = str(tmp_path / "out.pqax")
    refused(capsys, arguments(output), message, output)


@needs_pyarrow
def test_a_missing_mount_folder_exits_with_2_and_writes_nothing(capsys, tmp_path):
    output = str(tmp_path / "out.pqax")
    refused(capsys, ["--mount", str(FIXTURE / "no-such-folder"), "--output", output],
            "the folder given with --mount does not exist.", output)


SECRETS = [
    (["--client-id", CLIENT_ID, "--tenant", TENANT, "--client-secret", SECRET], "--client-secret"),
    (["--client-id", CLIENT_ID, "--tenant", TENANT, "--client-secret=" + SECRET], "--client-secret"),
    (["--access-token", SECRET], "--access-token"),
    (["--access-token=" + SECRET], "--access-token"),
    (["--client-id", CLIENT_ID, "--tenant", TENANT, "--client-certificate", "cert.pfx", "--client-certificate-password", SECRET],
     "--client-certificate-password"),
    (["--client-id", CLIENT_ID, "--tenant", TENANT, "--client-certificate", "cert.pfx", "--client-certificate-password=" + SECRET],
     "--client-certificate-password"),
]


@pytest.mark.parametrize("identity, option", SECRETS, ids=[f"{option} {index}" for index, (_, option) in enumerate(SECRETS)])
def test_a_secret_on_the_command_line_is_refused_without_echoing_it(capsys, tmp_path, identity, option):
    message = (f"{option} is not accepted: a secret on the command line stays in the shell history and in the list of "
               f"processes. Put it in an environment variable and give the name of the variable with {option}-env.")
    out, err = refused(capsys, workspace(tmp_path / "out.pqax", *identity), message, str(tmp_path / "out.pqax"))
    assert SECRET not in out + err
    assert "S3cret" not in out + err


@pytest.mark.parametrize("name", ["eyJ0.S3cret.Token", "S3cret=Value", "Sup3r S3cret"])
def test_a_name_that_is_not_a_variable_name_is_refused_without_echoing_it(capsys, tmp_path, name):
    out, err = refused(capsys, workspace(tmp_path / "out.pqax", "--access-token-env", name))
    assert "S3cret" not in out + err
    assert "environment variable" in err


def test_an_unknown_option_with_a_value_is_echoed_up_to_the_equals_sign(capsys, tmp_path):
    out, err = refused(capsys, workspace(tmp_path / "out.pqax", "--no-such-option=" + SECRET),
                       "unknown option '--no-such-option'.")
    assert SECRET not in out + err


def test_a_secret_on_the_command_line_is_refused_in_a_process_of_its_own(tmp_path):
    environment = dict(os.environ)
    environment["PYTHONPATH"] = os.pathsep.join(filter(None, (str(ROOT / "src" / "python"), environment.get("PYTHONPATH"))))
    done = subprocess.run([sys.executable, "-m", "pqax", "collect", *workspace(tmp_path / "out.pqax", "--client-id", CLIENT_ID,
                                                                              "--tenant", TENANT, "--client-secret", SECRET)],
                          capture_output=True, text=True, timeout=120, env=environment, cwd=str(ROOT))
    assert done.returncode == 2
    assert SECRET not in done.stdout + done.stderr


def test_collect_help_prints_the_usage(capsys):
    code, out, err = run_cli(["collect", "--help"], capsys)
    assert code == 0
    for option in ("--local", "--mount", "--workspace", "--item", "--output", "--endpoint", "--access-token-env",
                   "--azure-cli", "--depth", "--level", "--max-footers", "--max-commits", "--parallelism", "--verbose"):
        assert option in out, option


@needs_pyarrow  # the check of pyarrow comes right after the parsing, before the folder
def test_a_missing_folder_exits_with_2_and_writes_nothing(capsys, tmp_path):
    output = str(tmp_path / "out.pqax")
    refused(capsys, ["--local", str(FIXTURE / "no-such-folder"), "--output", output],
            "the folder given with --local does not exist.", output)


@pytest.mark.skipif(HAS_PYARROW, reason="pyarrow is installed")
def test_without_pyarrow_the_command_exits_with_2_and_names_the_extra(capsys, tmp_path):
    output = str(tmp_path / "out.pqax")
    out, err = refused(capsys, ["--local", LAKEHOUSE, "--output", output], output=output)
    assert "pqax[collect]" in err


# ---- --local ----

def assert_run(code, out, err, output, tables, footers, requests=None, bytes_read=None, found_items=None):
    """Exit code 0, the lines of the interface, never the name of a table or an item, and a valid file."""
    assert code == 0, out + err
    printed = lines(out)
    items = found_items if found_items is not None else len({table.item_folder for table in tables})
    assert printed[0] == f"Found {len(tables)} tables in {items} items."
    progress = [PROGRESS.match(line) for line in printed[1:-1]]
    assert all(progress), printed
    assert [int(match.group(1)) for match in progress] == list(range(1, len(tables) + 1))
    assert all(int(match.group(2)) == len(tables) for match in progress)
    assert sum(int(match.group(3)) for match in progress) == footers
    so_far = [int(match.group(5)) for match in progress]
    assert so_far == sorted(so_far)
    final = FINAL.match(printed[-1])
    assert final, printed[-1]
    assert final.group(1) == os.path.basename(output)
    assert (int(final.group(2)), int(final.group(3)), int(final.group(4))) == (len(tables), footers, 0)
    if requests is not None:
        assert int(final.group(5)) == requests
    if bytes_read is not None:
        assert int(final.group(6)) == bytes_read
    assert so_far[-1] <= int(final.group(5))
    for dump in TABLES:
        for name in (dump.item, dump.name):
            assert name not in out and name not in err, name
    assert pqax.validate(output).is_valid
    with pqax.open(output) as doc:
        collection = doc.manifest["collection"]
        assert (collection["collectorApp"], collection["collectorAppVersion"]) == ("pqax", pqax.__version__)
        assert (collection["collectorLib"], collection["collectorLibVersion"]) == ("pqax.collect", pqax.__version__)
        assert collection["requestCount"] == int(final.group(5))
        assert collection["bytesRead"] == int(final.group(6))
    return printed


@needs_pyarrow
def test_local_collects_the_fixture_and_prints_counts_only(capsys, tmp_path):
    output = str(tmp_path / "fixture.pqax")
    code, out, err = run_cli(["collect", "--local", LAKEHOUSE, "--output", output], capsys)
    assert_run(code, out, err, output, TABLES, 5, 17, 142178)
    assert err.strip() == ""
    with pqax.open(output) as doc, pqax.open(sample("sample.pqax")) as expected:
        for name in ("tables", "files", "rowGroups", "columnChunks", "commits"):
            assert doc.row_count(name) == expected.row_count(name)
        assert rows(doc, "issues") == []


@needs_pyarrow
@pytest.mark.parametrize("depth, file", [("summary", "summary-only.pqax"), ("files", "files.pqax"),
                                         ("rowGroups", "row-groups.pqax"), ("columnChunks", "sample.pqax")])
def test_the_depth_option_gives_the_rows_of_that_depth(capsys, tmp_path, depth, file):
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", "--local", LAKEHOUSE, "--output", output, "--depth", depth], capsys)
    assert_run(code, out, err, output, TABLES, 5, 17, 142178)
    with pqax.open(output) as doc, pqax.open(sample(file)) as expected:
        names = {name for part in expected.manifest["parts"] for name in part["tables"]}
        names |= {name for part in doc.manifest["parts"] for name in part["tables"]}
        for name in names:
            assert doc.row_count(name) == expected.row_count(name), name


@needs_pyarrow
def test_level_1_reads_no_footer(capsys, tmp_path):
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", "--local", LAKEHOUSE, "--output", output, "--level", "1"], capsys)
    assert_run(code, out, err, output, TABLES, 0, 12, 13841)
    with pqax.open(output) as doc:
        assert doc.manifest["collection"]["level"] == 1
        assert all(table["footerCount"] == 0 for table in rows(doc, "tables"))
        assert rows(doc, "rowGroups") == []


@needs_pyarrow
def test_max_footers_limits_the_footers_of_each_table(capsys, tmp_path):
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", "--local", LAKEHOUSE, "--output", output, "--max-footers", "1",
                              "--max-commits", "0", "--parallelism", "1"], capsys)
    assert_run(code, out, err, output, TABLES, 2)
    with pqax.open(output) as doc:
        assert [table["footerCount"] for table in rows(doc, "tables")] == [1, 1]


@needs_pyarrow
def test_item_collects_the_tables_of_one_item(capsys, tmp_path):
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", "--local", LAKEHOUSE, "--output", output, "--item", ORDERS.item_folder], capsys)
    assert_run(code, out, err, output, [ORDERS], 2, 6, 35921)
    with pqax.open(output) as doc:
        [table] = rows(doc, "tables")
        assert table["name"] == ORDERS.name


@needs_pyarrow
def test_a_folder_without_tables_exits_with_2(capsys, tmp_path):
    (tmp_path / "Empty.Lakehouse" / "Tables").mkdir(parents=True)
    output = str(tmp_path / "out.pqax")
    out, _ = refused(capsys, ["--local", str(tmp_path), "--output", output],
                     "no table found. An item is a folder such as Sales.Lakehouse, with a Tables folder.", output)
    assert lines(out) == ["Found 0 tables in 1 items."]


@needs_pyarrow
def test_the_output_folder_is_created(capsys, tmp_path):
    output = str(tmp_path / "new" / "folder" / "out.pqax")
    code, out, err = run_cli(["collect", "--local", LAKEHOUSE, "--output", output, "--item", ORDERS.item_folder], capsys)
    assert code == 0, out + err
    assert os.path.exists(output)


# ---- --mount ----

@needs_pyarrow
def test_mount_collects_the_item_of_a_folder(capsys, tmp_path):
    folder = copy_tree(FIXTURE / ORDERS.item_folder, tmp_path / "default")
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", "--mount", str(folder), "--item", ORDERS.item_folder, "--output", output], capsys)
    assert_run(code, out, err, output, [ORDERS], 2)
    with pqax.open(output) as doc:
        [table] = rows(doc, "tables")
        [source] = rows(doc, "sources")
        assert (source["name"], source["kind"]) == (ORDERS.item, "OneLake warehouse")
        assert source["location"] == os.path.abspath(str(folder))
        assert table["location"] == os.path.abspath(os.path.join(str(folder), "Tables", *ORDERS.namespace, ORDERS.name))


@needs_pyarrow
def test_mount_without_an_item_is_a_lakehouse_named_after_its_folder(capsys, tmp_path):
    folder = copy_tree(FIXTURE / SALES.item_folder, tmp_path / "default")
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", "--mount", str(folder), "--output", output], capsys)
    assert code == 0, out + err
    assert lines(out)[0] == "Found 1 tables in 1 items."
    assert FINAL.match(lines(out)[-1])
    with pqax.open(output) as doc:
        [source] = rows(doc, "sources")
        assert (source["name"], source["kind"]) == ("default", "OneLake lakehouse")
        [table] = rows(doc, "tables")
        assert table["fileCount"] == len(SALES.files)


# ---- --workspace with an access token, against the fake endpoint ----

def workspace_run(capsys, fake, output, *options, item=ORDERS.item_folder):
    return run_cli(["collect", "--workspace", WORKSPACE, "--item", item, "--output", output,
                    "--endpoint", fake.endpoint, *options], capsys)


@needs_pyarrow
def test_an_access_token_from_a_variable_collects_through_onelake(capsys, tmp_path, monkeypatch):
    monkeypatch.setenv("PQAX_TEST_TOKEN", TOKEN)
    output = str(tmp_path / "out.pqax")
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        code, out, err = workspace_run(capsys, fake, output, "--access-token-env", "PQAX_TEST_TOKEN")
        calls = fake.recorded()
    assert lines(out)[0] == "Signing in. The token stays in memory and is gone when the command ends."
    assert_run(code, out.split("\n", 1)[1], err, output, [ORDERS], 2)
    assert calls and all(call.authorization == "Bearer " + TOKEN for call in calls)
    assert all(call.method == "GET" for call in calls)
    assert TOKEN not in out + err
    assert err.strip() == ""
    with pqax.open(output) as doc:
        [table] = rows(doc, "tables")
        assert table["location"] == f"abfss://{WORKSPACE}@{fake.host}/{ORDERS.table_path}"
        assert doc.manifest["collection"]["requestCount"] <= len(calls)
    with open(output, "rb") as handle:
        assert TOKEN.encode() not in handle.read()


@needs_pyarrow
def test_verbose_prints_the_identity_and_every_request_and_never_the_token(capsys, tmp_path, monkeypatch):
    monkeypatch.setenv("PQAX_TEST_TOKEN", TOKEN)
    output = str(tmp_path / "out.pqax")
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        code, out, err = workspace_run(capsys, fake, output, "--access-token-env", "PQAX_TEST_TOKEN", "--verbose")
        calls = fake.recorded()
    assert code == 0, out + err
    printed = lines(err)
    requests = [line for line in printed if line.startswith("GET ")]
    assert len(requests) == len(calls)
    assert all(re.match(r"^GET http://127\.0\.0\.1:\d+/\S+ -> \d{3}$", line) for line in requests), requests
    assert printed and not printed[0].startswith("GET ")  # the identity comes first
    for hidden in (TOKEN, "Bearer"):
        assert hidden not in out + err


@needs_pyarrow
def test_an_expired_token_exits_with_2_before_any_request(capsys, tmp_path, monkeypatch):
    expiry = datetime.datetime(2020, 1, 2, 3, 4, 5, tzinfo=datetime.timezone.utc)
    monkeypatch.setenv("PQAX_TEST_TOKEN", jwt({"aud": "https://storage.azure.com", "exp": int(expiry.timestamp())}))
    output = str(tmp_path / "out.pqax")
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        code, out, err = workspace_run(capsys, fake, output, "--access-token-env", "PQAX_TEST_TOKEN")
        assert fake.recorded() == []
    assert code == 2, out + err
    assert "pqax collect: the access token of --access-token-env expired at 2020-01-02 03:04:05 UTC." in lines(err)
    assert not os.path.exists(output)


@needs_pyarrow
def test_a_token_for_another_audience_is_used_with_a_warning(capsys, tmp_path, monkeypatch):
    token = jwt({"aud": "https://database.windows.net/", "exp": int(time.time()) + 3600, "sub": "synthetic"})
    monkeypatch.setenv("PQAX_TEST_TOKEN", token)
    output = str(tmp_path / "out.pqax")
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        code, out, err = workspace_run(capsys, fake, output, "--access-token-env", "PQAX_TEST_TOKEN")
        assert all(call.authorization == "Bearer " + token for call in fake.recorded())
    assert code == 0, out + err
    assert ("pqax collect: the access token is for https://database.windows.net/, and OneLake takes one for "
            "https://storage.azure.com; it may refuse it.") in lines(err)
    assert token not in out + err


@needs_pyarrow
def test_a_token_for_storage_that_has_not_expired_gives_no_warning(capsys, tmp_path, monkeypatch):
    token = jwt({"aud": "https://storage.azure.com", "exp": int(time.time()) + 3600})
    monkeypatch.setenv("PQAX_TEST_TOKEN", token)
    output = str(tmp_path / "out.pqax")
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        code, out, err = workspace_run(capsys, fake, output, "--access-token-env", "PQAX_TEST_TOKEN")
    assert code == 0, out + err
    assert err.strip() == ""


@needs_pyarrow
def test_a_variable_that_is_not_set_exits_with_2_without_its_name(capsys, tmp_path, monkeypatch):
    monkeypatch.delenv("PQAX_NAME_OF_A_VARIABLE", raising=False)
    output = str(tmp_path / "out.pqax")
    with FakeDfs(FIXTURE, WORKSPACE) as fake:
        code, out, err = workspace_run(capsys, fake, output, "--access-token-env", "PQAX_NAME_OF_A_VARIABLE")
        assert fake.recorded() == []
    assert code == 2, out + err
    assert "--access-token-env" in err
    assert "PQAX_NAME_OF_A_VARIABLE" not in out + err
    assert not os.path.exists(output)


@needs_pyarrow
@pytest.mark.parametrize("status, words", [(403, ("403", "Contributor")), (401, ("401", "tenant"))])
def test_a_refused_request_exits_with_2_and_one_line_with_the_likely_cause(capsys, tmp_path, monkeypatch, status, words):
    monkeypatch.setenv("PQAX_TEST_TOKEN", TOKEN)
    output = str(tmp_path / "out.pqax")
    body = b'{"error":{"code":"Refused"}}'
    with FakeDfs(FIXTURE, WORKSPACE, answer=lambda call: (status, {"Content-Type": "application/json"}, body)) as fake:
        code, out, err = workspace_run(capsys, fake, output, "--access-token-env", "PQAX_TEST_TOKEN")
        assert fake.recorded()
    assert code == 2, out + err
    [line] = lines(err)
    assert line.startswith("pqax collect: ")
    for word in words:
        assert word.lower() in line.lower(), line
    assert TOKEN not in out + err
    assert not os.path.exists(output)


# ---- identities that need a program or a library ----

def test_azure_cli_without_az_on_the_path_exits_with_2(capsys, tmp_path, monkeypatch):
    empty = tmp_path / "empty-path"
    empty.mkdir()
    monkeypatch.setenv("PATH", str(empty))
    output = str(tmp_path / "out.pqax")
    started = time.monotonic()
    code, out, err = run_cli(["collect", "--workspace", WORKSPACE, "--item", ORDERS.item_folder, "--output", output,
                              "--endpoint", "http://127.0.0.1:9", "--azure-cli"], capsys)
    assert code == 2, out + err
    assert any(line.startswith("pqax collect: ") for line in lines(err)), err
    assert not os.path.exists(output)
    assert time.monotonic() - started < 60


AZURE_IDENTITIES = [
    ([], {}),
    (["--device-code"], {}),
    (["--browser"], {}),
    (["--managed-identity"], {}),
    (["--client-id", CLIENT_ID, "--tenant", TENANT, "--client-secret-env", "PQAX_TEST_SECRET"], {"PQAX_TEST_SECRET": SECRET}),
    (["--env-credential"], {"AZURE_TENANT_ID": TENANT, "AZURE_CLIENT_ID": CLIENT_ID, "AZURE_CLIENT_SECRET": SECRET}),
]


@needs_pyarrow
@pytest.mark.parametrize("identity, variables", AZURE_IDENTITIES,
                         ids=["default", "device code", "browser", "managed identity", "client secret", "env credential"])
def test_an_identity_of_azure_identity_without_the_library_exits_with_2_naming_the_extra(tmp_path, identity, variables):
    script = ("import sys\n"
              "for name in ('azure.identity', 'azure.identity.aio', 'azure.core', 'azure'): sys.modules[name] = None\n"
              "from pqax.__main__ import main\n"
              "sys.exit(main(sys.argv[1:]))\n")
    environment = dict(os.environ)
    environment.update(variables)
    environment["PYTHONPATH"] = os.pathsep.join(filter(None, (str(ROOT / "src" / "python"), environment.get("PYTHONPATH"))))
    output = str(tmp_path / "out.pqax")
    arguments = ["collect", "--workspace", WORKSPACE, "--item", ORDERS.item_folder, "--output", output,
                 "--endpoint", "http://127.0.0.1:9", *identity]
    done = subprocess.run([sys.executable, "-c", script, *arguments], capture_output=True, text=True, timeout=120,
                          env=environment, cwd=str(ROOT))
    assert done.returncode == 2, done.stdout + done.stderr
    assert "pqax[azure]" in done.stderr
    assert SECRET not in done.stdout + done.stderr
    assert not os.path.exists(output)


@pytest.mark.skipif(HAS_AZURE_IDENTITY, reason="azure-identity is installed")
@needs_pyarrow
def test_the_device_code_without_azure_identity_exits_with_2_naming_the_extra(capsys, tmp_path):
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", "--workspace", WORKSPACE, "--item", ORDERS.item_folder, "--output", output,
                              "--endpoint", "http://127.0.0.1:9", "--device-code"], capsys)
    assert code == 2, out + err
    assert "pqax[azure]" in err
