"""python -m pqax collect --adls: a folder of a container of Azure Data Lake Storage Gen2, from CollectAdlsTests.cs of
the C# command line tests, the Collect section and the table "--adls refuses" of the README of Pqax.Cli, and sections
4.5 and 4.6 of docs/contracts.md. The forms of the URL, the lines that the command prints, the footprint of a folder,
the errors of the command line and their messages, the identities that it shares with --workspace, and the line of a
401 and of a 403 of a storage account.

The command runs in the process, through pqax.__main__.main, with an access token from a variable and the fake DFS
endpoint (collect_kit.FakeDfs), which serves the synthetic container as the filesystem lake of the account pqaxtest;
the HTTPS connections of the command go to it (collect_kit.HttpsRedirect). The runs that collect need pyarrow, the
extra pqax[collect], which the command asks for before it reads anything; a wrong command line is refused before that.
"""
from __future__ import annotations

import os
import re
import time

import pytest

import pqax
from collect_kit import (ACCOUNT_ENDPOINT, ACCOUNT_HOST, CONTAINER, FILESYSTEM, HAS_PYARROW, NEWER, OLDER, Container,
                         FakeDfs, HttpsRedirect, error_answer, jwt, listed_directory, of_table, rows, run_cli)
from pqax.collect import _command

needs_pyarrow = pytest.mark.skipif(not HAS_PYARROW, reason="collecting needs pyarrow, pqax[collect]")
URL = ACCOUNT_ENDPOINT + "/lake/data"
TOKEN = "fake-access-token-for-the-adls-tests-3K8"
TENANT = "00000000-1111-2222-3333-444444444444"
CLIENT_ID = "55555555-6666-7777-8888-999999999999"
SIGNATURE = "SyntheticSignatureValue0123"
PROGRESS = re.compile(r"^  table (\d+) of (\d+): (\d+) footers, (\d+) issues; (\d+) requests so far$")
FINAL = re.compile(r"^(.+): (\d+) tables, (\d+) footers read, (\d+) issues, (\d+) requests, (\d+) bytes read; valid$")


def lines(text):
    return [line.rstrip("\r") for line in text.splitlines() if line.strip()]


@pytest.fixture
def lake(tmp_path):
    """Under data, a copy of the Delta table of the fixture, a folder of two of its Parquet files, and a folder of other
    files; under my data, another folder of Parquet files."""
    lake = Container(tmp_path / "lake")
    lake.copy_delta("data/sales")
    for folder in ("data/raw", "my data/raw"):
        lake.copy_file("part-0001.parquet", f"{folder}/part-0001.parquet", OLDER)
        lake.copy_file("part-0002.parquet", f"{folder}/part-0002.parquet", NEWER)
        lake.write(f"{folder}/_SUCCESS", "")
    lake.write("data/other/a.csv", "id,name\n1,synthetic\n")
    lake.write("data/other/b.json", '{"id":1}\n')
    return lake


@pytest.fixture
def account(lake, monkeypatch):
    """The fake endpoint of the account, with the HTTPS connections of the command sent to it and the token in the
    variable PQAX_TEST_TOKEN."""
    monkeypatch.setenv("PQAX_TEST_TOKEN", TOKEN)
    with FakeDfs(lake.root, FILESYSTEM) as fake:
        fake.redirect = HttpsRedirect(fake, monkeypatch)
        yield fake


def collect(capsys, output, url, *options, identity=("--access-token-env", "PQAX_TEST_TOKEN")):
    return run_cli(["collect", "--adls", url, "--output", output, *identity, *options], capsys)


# ---- a collection ----

@needs_pyarrow
@pytest.mark.parametrize("url", [URL, URL + "/", CONTAINER + "/data", CONTAINER + "/data/"])
def test_a_folder_of_a_container_is_collected_with_the_token_of_the_variable(capsys, tmp_path, account, url):
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, url)
    calls = account.recorded()
    assert code == 0, out + err
    printed = lines(out)
    # The lines of a workspace collection, after the count of the tables of the folder; never the name of a table, of
    # the folder, or the token.
    assert printed[0] == "Signing in. The token stays in memory and is gone when the command ends."
    assert printed[1] == "Found 2 tables in the folder."
    progress = [PROGRESS.match(line) for line in printed[2:-1]]
    assert len(progress) == 2 and all(progress), printed
    final = FINAL.match(printed[-1])
    assert final, printed[-1]
    assert (final.group(1), final.group(2), final.group(4)) == ("out.pqax", "2", "0")
    for hidden in ("sales", "raw", "data", TOKEN, "Bearer"):
        assert hidden not in out + err, hidden
    assert err.strip() == ""
    # Every request goes to the account, with the token, and reads the filesystem of the URL.
    assert calls and all(call.authorization == "Bearer " + TOKEN and call.method == "GET" for call in calls)
    assert set(account.redirect.hosts) == {ACCOUNT_HOST}
    assert all(call.path == "/lake" or call.path.startswith("/lake/") for call in calls)
    # One source, the folder, with a Delta table and a Parquet table.
    assert pqax.validate(output).is_valid
    with pqax.open(output) as doc:
        [source] = rows(doc, "sources")
        assert (source["kind"], source["name"], source["location"]) == ("Storage folder", "data", CONTAINER + "/data")
        assert source.get("identifiers") is None
        assert sorted((table["name"], table["format"], table["location"]) for table in rows(doc, "tables")) == [
            ("raw", "Parquet", CONTAINER + "/data/raw"), ("sales", "Delta", CONTAINER + "/data/sales")]
        assert rows(doc, "issues") == []
        collection = doc.manifest["collection"]
        assert (collection["level"], collection["collectorApp"], collection["collectorLib"]) == (2, "pqax",
                                                                                                "pqax.collect")
        assert (collection["requestCount"], collection["bytesRead"]) == (int(final.group(5)), int(final.group(6)))
    with open(output, "rb") as handle:
        assert TOKEN.encode() not in handle.read()


@needs_pyarrow
@pytest.mark.parametrize("url", [ACCOUNT_ENDPOINT + "/lake", ACCOUNT_ENDPOINT + "/lake/", CONTAINER, CONTAINER + "/"])
def test_the_root_of_a_container_is_collected_as_the_container(capsys, tmp_path, account, url):
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, url)
    assert code == 0, out + err
    assert "Found 3 tables in the container." in lines(out)
    assert "directory" not in account.recorded()[0].query  # the root is listed without the directory parameter
    with pqax.open(output) as doc:
        [source] = rows(doc, "sources")
        assert (source["name"], source["location"]) == (FILESYSTEM, CONTAINER)
        tables = rows(doc, "tables")
        assert len(tables) == 3
        assert [table["location"] for table in tables if table["name"] == "raw"] == [CONTAINER + "/data/raw",
                                                                                     CONTAINER + "/my data/raw"]
        assert [table["namespace"] for table in tables if table["location"].endswith("/my data/raw")] == [["my data"]]


@needs_pyarrow
@pytest.mark.parametrize("url", [ACCOUNT_ENDPOINT + "/lake/my%20data", CONTAINER + "/my%20data",
                                 CONTAINER + "/my data", ACCOUNT_ENDPOINT + "/lake/data/../my%20data/."])
def test_the_segments_of_the_url_are_decoded(capsys, tmp_path, account, url):
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, url)
    assert code == 0, out + err
    assert listed_directory(account.recorded()[0]) == "my data"
    with pqax.open(output) as doc:
        [source] = rows(doc, "sources")
        assert (source["name"], source["location"]) == ("my data", CONTAINER + "/my data")
        [table] = rows(doc, "tables")
        assert (table["format"], table["location"]) == ("Parquet", CONTAINER + "/my data/raw")


@needs_pyarrow
def test_the_host_of_the_url_is_written_in_lower_case_and_the_default_port_is_left_out(capsys, tmp_path, account):
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, "https://PQAXTEST.DFS.core.windows.net:443/lake/data")
    assert code == 0, out + err
    with pqax.open(output) as doc:
        [source] = rows(doc, "sources")
        assert source["location"] == CONTAINER + "/data"


@needs_pyarrow
def test_the_options_of_a_collection_apply_to_a_container(capsys, tmp_path, account):
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, URL, "--level", "1", "--depth", "summary")
    assert code == 0, out + err
    with pqax.open(output) as doc:
        assert doc.manifest["collection"]["level"] == 1
        assert all((table["footerCount"], table["detailDepth"]) == (0, "Summary") for table in rows(doc, "tables"))


@needs_pyarrow
@pytest.mark.parametrize("url", [URL + "/other", URL + "/no_such_folder"])
def test_a_folder_with_no_table_exits_with_2_and_writes_nothing(capsys, tmp_path, account, url):
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, url)
    assert code == 2, out + err
    assert "Found 0 tables in the folder." in lines(out)
    assert lines(err) == ["pqax collect: no table found. A table is a folder with _delta_log, or a folder of Parquet "
                          "files with no subfolder, at most 4 levels below the folder of --adls."]
    assert not os.path.exists(output)


@needs_pyarrow
def test_verbose_prints_the_identity_and_every_request_to_the_account_and_never_the_token(capsys, tmp_path, account):
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, URL, "--verbose")
    assert code == 0, out + err
    printed = lines(err)
    assert printed[0] == "Sign-in: none; the access token of --access-token-env."
    requests = printed[1:]
    assert len(requests) == len(account.recorded())
    assert all(re.match(r"^GET https://pqaxtest\.dfs\.core\.windows\.net/lake[/?]\S* -> \d{3}$", line)
               for line in requests), requests
    for hidden in (TOKEN, "Bearer"):
        assert hidden not in out + err


@needs_pyarrow
def test_a_token_for_another_audience_is_used_with_a_warning_that_names_the_storage_account(capsys, tmp_path, account,
                                                                                            monkeypatch):
    token = jwt({"aud": "https://database.windows.net/", "exp": int(time.time()) + 3600})
    monkeypatch.setenv("PQAX_TEST_TOKEN", token)
    output = str(tmp_path / "out.pqax")
    code, out, err = collect(capsys, output, URL)
    assert code == 0, out + err
    assert lines(err) == ["pqax collect: the access token is for https://database.windows.net/, and the storage "
                          "account takes one for https://storage.azure.com; it may refuse it."]
    assert all(call.authorization == "Bearer " + token for call in account.recorded())


# ---- the answers of the storage account ----

def refusing(lake, monkeypatch, status, code):
    monkeypatch.setenv("PQAX_TEST_TOKEN", TOKEN)
    fake = FakeDfs(lake.root, FILESYSTEM, answer=lambda call: error_answer(status, code))
    fake.__enter__()
    HttpsRedirect(fake, monkeypatch)
    return fake


@needs_pyarrow
def test_a_403_of_the_account_exits_with_2_and_one_line_that_names_the_data_role(capsys, tmp_path, lake, monkeypatch):
    fake = refusing(lake, monkeypatch, 403, "AuthorizationPermissionMismatch")
    try:
        output = str(tmp_path / "out.pqax")
        code, out, err = collect(capsys, output, URL)
        calls = fake.recorded()
    finally:
        fake.__exit__(None, None, None)
    assert code == 2, out + err
    [line] = lines(err)
    assert line == ("pqax collect: The storage account answered 403 Forbidden (AuthorizationPermissionMismatch): the "
                    "identity cannot read the container. It needs the role Storage Blob Data Reader on the container "
                    "or the account, or ACLs that give it read and execute on the folders; Reader, Contributor, or "
                    "Owner of the account alone do not read data. A network rule of the account also answers 403.")
    assert "workspace" not in line.lower()
    assert TOKEN not in out + err
    assert calls and all(call.authorization == "Bearer " + TOKEN for call in calls)
    assert not os.path.exists(output)


@needs_pyarrow
def test_a_401_of_the_account_exits_with_2_and_one_line_that_names_the_audience_and_the_tenant(capsys, tmp_path, lake,
                                                                                               monkeypatch):
    fake = refusing(lake, monkeypatch, 401, "InvalidAuthenticationInfo")
    try:
        code, out, err = collect(capsys, str(tmp_path / "out.pqax"), URL)
    finally:
        fake.__exit__(None, None, None)
    assert code == 2, out + err
    [line] = lines(err)
    assert line == ("pqax collect: The storage account answered 401 Unauthorized (InvalidAuthenticationInfo): the "
                    "storage account refused the token. An access token must be for https://storage.azure.com, from "
                    "the tenant of the account, and not expired.")
    assert "Fabric" not in line


@needs_pyarrow
def test_another_failure_of_the_account_exits_with_2_and_names_the_storage_account(capsys, tmp_path, lake, monkeypatch):
    # A status that is not retried, so that the test does not wait for the retries of a 500.
    fake = refusing(lake, monkeypatch, 400, "InvalidQueryParameterValue")
    try:
        code, out, err = collect(capsys, str(tmp_path / "out.pqax"), URL)
    finally:
        fake.__exit__(None, None, None)
    assert code == 2, out + err
    assert lines(err) == ["pqax collect: StorageError: The storage account answered 400 Bad Request "
                          "(InvalidQueryParameterValue)."]


def test_the_lines_of_onelake_stay_those_of_a_workspace():
    from pqax.collect import StorageError
    forbidden = StorageError("OneLake answered 403 Forbidden.", 403)
    unauthorized = StorageError("OneLake answered 401 Unauthorized.", 401)
    for line, words in ((_command.describe(forbidden), ("403", "workspace", "Contributor")),
                        (_command.describe(unauthorized), ("401", "tenant", "Fabric"))):
        assert "\n" not in line
        assert all(word in line for word in words), line
    assert _command.describe(forbidden) == _command.describe(forbidden, storage_account=False)
    account = _command.describe(StorageError("The storage account answered 403 Forbidden.", 403), storage_account=True)
    assert all(word.lower() in account.lower() for word in ("403", "Storage Blob Data Reader", "container", "account",
                                                             "ACL", "read", "execute", "network"))


# ---- the command line ----

SOURCES = "give one of --local, --mount, --workspace, or --adls."
FORM = "--adls takes https://<account>.dfs.core.windows.net/<container>[/<folder>]."
MISSING = "--adls takes https://<account>.dfs.core.windows.net/<container>[/<folder>]: the container is missing."
NAME = "the container of --adls is not a valid name: 3 to 63 lowercase letters, digits, and dashes."
DOTS = ("--adls takes https://<account>.dfs.core.windows.net/<container>[/<folder>]: the folder cannot hold . or .. "
        "segments.")
BLOB = "--adls takes the DFS endpoint of the account, <account>.dfs.core.windows.net, not the blob endpoint."
ONELAKE = "--adls is for a storage account; for OneLake, use --workspace and --item."
SAS = ("--adls takes a URL without a query: a SAS token is not accepted, since it would stay in the shell history. "
       "Sign in with one of the identities.")


#: The command lines that --adls refuses, before --output and the identity: a name, the arguments, and the message.
WRONG_COMMAND_LINES = [
    ("with --local", ["--adls", URL, "--local", "."], SOURCES),
    ("with --mount", ["--adls", URL, "--mount", "."], SOURCES),
    ("with --workspace", ["--adls", URL, "--workspace", "W", "--item", "I.Lakehouse"], SOURCES),
    ("with --item", ["--adls", URL, "--item", "I.Lakehouse"],
     "--item does not apply to --adls: the tables are found under the folder of the URL."),
    ("with --endpoint", ["--adls", URL, "--endpoint", ACCOUNT_ENDPOINT], "--endpoint applies to --workspace only."),
    ("no value", ["--adls"], "--adls needs a value."),
    ("http", ["--adls", "http://pqaxtest.dfs.core.windows.net/lake/data"], FORM),
    ("s3", ["--adls", "s3://lake/data"], FORM),
    ("no scheme", ["--adls", "pqaxtest.dfs.core.windows.net/lake/data"], FORM),
    ("user in https", ["--adls", "https://someone@pqaxtest.dfs.core.windows.net/lake"], FORM),
    ("bad port", ["--adls", "https://pqaxtest.dfs.core.windows.net:99999/lake"], FORM),
    ("escaped host", ["--adls", "https://pqax%74est.dfs.core.windows.net/lake"], FORM),
    # A backslash is a slash in the path only: right after the host, it is part of the host.
    ("backslash after the host", ["--adls", ACCOUNT_ENDPOINT + "\\lake\\data"], FORM),
    ("backslash after the host abfss", ["--adls", "abfss://lake@pqaxtest.dfs.core.windows.net\\data"], FORM),
    ("blob host", ["--adls", "https://pqaxtest.blob.core.windows.net/lake/data"], BLOB),
    ("blob host abfss", ["--adls", "abfss://lake@pqaxtest.blob.core.windows.net/data"], BLOB),
    ("onelake host", ["--adls", "https://onelake.dfs.fabric.microsoft.com/Workspace/Item.Lakehouse/Tables"], ONELAKE),
    ("onelake host abfss", ["--adls", "abfss://work@onelake.dfs.fabric.microsoft.com/Item.Lakehouse"], ONELAKE),
    ("regional onelake host", ["--adls", "https://westeurope-onelake.dfs.fabric.microsoft.com/w/x"], ONELAKE),
    ("no filesystem", ["--adls", ACCOUNT_ENDPOINT], MISSING),
    ("no filesystem slash", ["--adls", ACCOUNT_ENDPOINT + "/"], MISSING),
    ("no filesystem abfss", ["--adls", "abfss://pqaxtest.dfs.core.windows.net/data"], MISSING),
    ("filesystem gone with ..", ["--adls", ACCOUNT_ENDPOINT + "/lake/.."], MISSING),
    ("encoded slash and ..", ["--adls", ACCOUNT_ENDPOINT + "/lake/x%2F..%2Fy"], DOTS),
    ("encoded slash and .", ["--adls", ACCOUNT_ENDPOINT + "/lake/x%2F."], DOTS),
    ("encoded slash and encoded ..", ["--adls", ACCOUNT_ENDPOINT + "/lake/x%2F%2E%2E%2Fy"], DOTS),
    ("encoded slash and .. abfss", ["--adls", CONTAINER + "/..%2Fy"], DOTS),
    ("uppercase filesystem", ["--adls", ACCOUNT_ENDPOINT + "/Lake/data"], NAME),
    ("uppercase filesystem abfss", ["--adls", "abfss://Lake@pqaxtest.dfs.core.windows.net/data"], NAME),
    ("short filesystem", ["--adls", ACCOUNT_ENDPOINT + "/ab/data"], NAME),
    ("long filesystem", ["--adls", ACCOUNT_ENDPOINT + "/" + "a" * 64 + "/data"], NAME),
    ("underscore filesystem", ["--adls", ACCOUNT_ENDPOINT + "/my_lake/data"], NAME),
    ("double dash filesystem", ["--adls", ACCOUNT_ENDPOINT + "/my--lake/data"], NAME),
    ("dash at the end", ["--adls", ACCOUNT_ENDPOINT + "/lake-/data"], NAME),
    ("newline filesystem", ["--adls", ACCOUNT_ENDPOINT + "/lake%0A/data"], NAME),
    ("newline filesystem abfss", ["--adls", "abfss://lake%0A@pqaxtest.dfs.core.windows.net/data"], NAME),
    ("password in abfss", ["--adls", "abfss://lake:x@pqaxtest.dfs.core.windows.net/data"], NAME),
    ("sas", ["--adls", URL + "?sv=2024-01-01&sp=rl&sig=" + SIGNATURE], SAS),
    ("sas abfss", ["--adls", CONTAINER + "/data?sig=" + SIGNATURE], SAS),
    ("sas fragment", ["--adls", URL + "#sig=" + SIGNATURE], SAS),
]


@pytest.mark.parametrize("name, arguments, message", WRONG_COMMAND_LINES,
                         ids=[name for name, _, _ in WRONG_COMMAND_LINES])
def test_a_wrong_command_line_with_adls_exits_with_2_before_any_request(capsys, tmp_path, account, name, arguments,
                                                                       message):
    output = str(tmp_path / "out.pqax")
    code, out, err = run_cli(["collect", *arguments, "--output", output, "--access-token-env", "PQAX_TEST_TOKEN"],
                             capsys)
    assert code == 2, out + err
    assert lines(err)[0] == "pqax collect: " + message
    assert lines(err)[1] == "Usage:"
    assert out == ""
    # A URL with a query is never quoted: the query can be a SAS token.
    if name.startswith("sas"):
        for hidden in (SIGNATURE, "sig=", "sv=2024", "pqaxtest"):
            assert hidden not in out + err, hidden
    assert account.recorded() == []
    assert not os.path.exists(output)


def test_a_url_without_output_is_refused_before_any_request(capsys, account):
    code, out, err = run_cli(["collect", "--adls", URL, "--access-token-env", "PQAX_TEST_TOKEN"], capsys)
    assert code == 2, out + err
    assert lines(err)[0] == "pqax collect: --output is missing."
    assert account.recorded() == []


@pytest.mark.parametrize("url, endpoint, file_system, folder", [
    (URL, ACCOUNT_ENDPOINT, "lake", "data"),
    (CONTAINER + "/data/", ACCOUNT_ENDPOINT, "lake", "data"),
    (CONTAINER, ACCOUNT_ENDPOINT, "lake", ""),
    (ACCOUNT_ENDPOINT + "/lake//sales//curated/", ACCOUNT_ENDPOINT, "lake", "sales/curated"),
    ("HTTPS://PQAXTEST.DFS.Core.Windows.Net/lake/Data", ACCOUNT_ENDPOINT, "lake", "Data"),
    ("ABFSS://lake@PQAXTEST.dfs.core.windows.net/Data", ACCOUNT_ENDPOINT, "lake", "Data"),
    (ACCOUNT_ENDPOINT + ":443/lake", ACCOUNT_ENDPOINT, "lake", ""),
    (CONTAINER + ":443/x", ACCOUNT_ENDPOINT, "lake", "x"),
    (ACCOUNT_ENDPOINT + ":8443/lake/x", ACCOUNT_ENDPOINT + ":8443", "lake", "x"),
    (ACCOUNT_ENDPOINT + "/lake/my%20data/caf%C3%A9", ACCOUNT_ENDPOINT, "lake", "my data/café"),
    (ACCOUNT_ENDPOINT + "/la%6Be/a%2Fb", ACCOUNT_ENDPOINT, "lake", "a/b"),
    (ACCOUNT_ENDPOINT + "/lake/a/../b/./c", ACCOUNT_ENDPOINT, "lake", "b/c"),
    (ACCOUNT_ENDPOINT + "/lake/a/%2E%2E/b", ACCOUNT_ENDPOINT, "lake", "b"),
    # A .. takes away the segment before it, an empty one included, as RFC 3986 says.
    (ACCOUNT_ENDPOINT + "/lake/a//../b", ACCOUNT_ENDPOINT, "lake", "a/b"),
    (ACCOUNT_ENDPOINT + "/lake/a//../../b", ACCOUNT_ENDPOINT, "lake", "b"),
    (CONTAINER + "/x/a//.././../b", ACCOUNT_ENDPOINT, "lake", "x/b"),
    (CONTAINER + "/a\\b", ACCOUNT_ENDPOINT, "lake", "a/b"),
    (ACCOUNT_ENDPOINT + "/lake/a%25b", ACCOUNT_ENDPOINT, "lake", "a%b"),
    (" " + URL + " ", ACCOUNT_ENDPOINT, "lake", "data"),
    ("https://pqaxtest.dfs.core.chinacloudapi.cn/lake/data", "https://pqaxtest.dfs.core.chinacloudapi.cn", "lake",
     "data"),
    (ACCOUNT_ENDPOINT + "/" + "a" * 63 + "/data", ACCOUNT_ENDPOINT, "a" * 63, "data"),
    (ACCOUNT_ENDPOINT + "/abc/data", ACCOUNT_ENDPOINT, "abc", "data"),
])
def test_the_url_names_the_endpoint_the_container_and_the_folder_as_in_csharp(url, endpoint, file_system, folder):
    options, problem = _command.parse(["--adls", url, "--output", "out.pqax"])
    assert problem is None
    assert (options.adls_endpoint, options.adls_file_system, options.adls_folder) == (endpoint, file_system, folder)


# ---- the identity options ----

IDENTITIES = [
    [], ["--device-code"], ["--device-code", "--tenant", TENANT], ["--browser", "--tenant", TENANT],
    ["--client-id", CLIENT_ID, "--tenant", TENANT, "--client-secret-env", "PQAX_SECRET"],
    ["--client-id", CLIENT_ID, "--tenant", TENANT, "--client-certificate", "cert.pfx"],
    ["--client-certificate", "cert.pfx", "--client-certificate-password-env", "PQAX_PFX", "--client-id", CLIENT_ID,
     "--tenant", TENANT],
    ["--managed-identity"], ["--managed-identity", "--client-id", CLIENT_ID], ["--azure-cli"],
    ["--azure-cli", "--tenant", TENANT], ["--access-token-env", "PQAX_TOKEN"], ["--env-credential"],
]
IDENTITY_FIELDS = ("mode", "tenant", "client_id", "secret_variable", "certificate", "password_variable",
                   "token_variable")


@pytest.mark.parametrize("identity", IDENTITIES, ids=lambda identity: " ".join(identity) or "none")
def test_each_identity_parses_with_adls_as_with_workspace(identity):
    workspace, problem = _command.parse(["--workspace", "W", "--item", "I.Lakehouse", "--output", "o.pqax", *identity])
    assert problem is None
    adls, problem = _command.parse(["--adls", URL, "--output", "o.pqax", *identity])
    assert problem is None, problem
    assert [getattr(adls, name) for name in IDENTITY_FIELDS] == [getattr(workspace, name) for name in IDENTITY_FIELDS]
    assert (adls.workspace, adls.local, adls.mount, adls.items, adls.output) == (None, None, None, [], "o.pqax")


WRONG_IDENTITIES = [
    ["--device-code", "--browser"], ["--azure-cli", "--managed-identity"], ["--client-secret-env", "PQAX_SECRET"],
    ["--client-id", CLIENT_ID, "--client-certificate", "cert.pfx"], ["--managed-identity", "--tenant", TENANT],
    ["--env-credential", "--tenant", TENANT], ["--client-certificate-password-env", "PQAX_PFX"],
    ["--access-token-env", "not-a-name"], ["--client-secret", "Synthetic~Secret.Value-0123"],
    ["--access-token=Synthetic~Token.Value-0123"],
]


@pytest.mark.parametrize("identity", WRONG_IDENTITIES, ids=lambda identity: " ".join(identity))
def test_a_wrong_identity_is_refused_with_adls_as_with_workspace(capsys, tmp_path, identity):
    output = str(tmp_path / "out.pqax")
    code, _, workspace = run_cli(["collect", "--workspace", "W", "--item", "I.Lakehouse", "--output", output,
                                  *identity], capsys)
    assert code == 2
    code, _, adls = run_cli(["collect", "--adls", URL, "--output", output, *identity], capsys)
    assert code == 2
    assert lines(adls)[0] == lines(workspace)[0]
    assert "Synthetic~" not in adls


@pytest.mark.parametrize("option", ["--azure-cli", "--managed-identity"])
def test_an_identity_is_still_refused_with_local(capsys, tmp_path, option):
    code, _, err = run_cli(["collect", "--local", str(tmp_path), "--output", str(tmp_path / "o.pqax"), option], capsys)
    assert code == 2
    assert lines(err)[0] == "pqax collect: --tenant, --client-id, and the identity options apply to --workspace and " \
                            "--adls only."


def test_a_url_without_identity_options_signs_in_with_a_device_code_by_default():
    options, problem = _command.parse(["--adls", URL, "--output", "o.pqax"])
    assert problem is None
    assert options.mode == "device-code"


def test_the_usage_names_adls_beside_workspace_and_endpoint(capsys):
    code, out, _ = run_cli(["collect", "--help"], capsys)
    assert code == 0
    assert ("  python -m pqax collect --adls https://<account>.dfs.core.windows.net/<container>[/<folder>] --output "
            "<file.pqax> [identity] [options]") in lines(out)
    for word in ("--workspace", "--endpoint", "Storage Blob", "Data Reader", "4 levels"):
        assert word in out, word
