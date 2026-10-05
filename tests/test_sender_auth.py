import email

from feed.mail import sender_rejection

FROM = "reports@v.example"
GOOD_RESULTS = (
    "mx.google.com; dkim=pass header.i=@v.example;\n"
    " spf=pass smtp.mailfrom=bounce@v.example;\n dmarc=pass header.from=v.example"
)


def message(sender: str = FROM, *results: str, from_header: str | None = None):
    headers = "".join(f"Authentication-Results: {r}\n" for r in results)
    from_header = from_header or f"From: Reports <{sender}>\n"
    return email.message_from_string(f"{headers}{from_header}Subject: feed\n\nbody")


def reason(*args) -> str:
    rejection = sender_rejection(*args)
    assert rejection is not None
    return rejection


def test_authenticated_sender_is_accepted():
    assert sender_rejection(message(FROM, GOOD_RESULTS), FROM, "mx.google.com") is None


def test_gmails_real_header_shape_is_accepted():
    results = (
        'mx.google.com;\n       dkim=pass header.i=@v.example header.s=abc header.b="IRCP1r/p";'
        "\n       spf=pass (google.com: domain of 0100@ses.v.example designates 54.240.9.99 as"
        " permitted sender) smtp.mailfrom=0100@ses.v.example;\n"
        "       dmarc=pass (p=NONE sp=NONE dis=NONE) header.from=v.example"
    )
    assert sender_rejection(message(FROM, results), FROM, "mx.google.com") is None


def test_dmarc_text_injected_into_the_spf_comment_is_ignored():
    # The envelope address is the attacker's and is echoed inside the SPF comment. A quoted
    # local part can hold spaces, so the fake clause ends exactly at the sender's domain.
    results = (
        'mx.google.com; spf=pass (google.com: domain of "dmarc=pass header.from=v.example x"'
        '@e.example designates 1.2.3.4) smtp.mailfrom="dmarc=pass header.from=v.example x"'
        "@e.example; dmarc=fail header.from=v.example"
    )
    assert sender_rejection(message(FROM, results), FROM, "mx.google.com") is not None


def test_a_comment_closed_early_by_the_sender_fails_closed():
    # A ")" in a quoted envelope address would end the comment early and expose a fake clause,
    # even with no real DMARC clause after it.
    for results in (
        'mx.google.com; spf=pass (google.com: domain of "x); dmarc=pass header.from=v.example'
        ' (y"@e.example designates 1.2.3.4) smtp.mailfrom=x@e.example',
        "mx.google.com; spf=pass (google.com: domain of (x) dmarc=pass header.from=v.example",
        "mx.google.com; dmarc=pass header.from=v.example (unterminated",
        "mx.google.com; dmarc=pass header.from=v.example) x",
    ):
        assert "ambiguous" in reason(message(FROM, results), FROM, "mx.google.com")


def test_a_second_dmarc_clause_is_treated_as_injected():
    results = (
        "mx.google.com; spf=pass smtp.mailfrom=x;dmarc=pass header.from=v.example@e.example;"
        " dmarc=fail header.from=v.example"
    )
    assert "DMARC" in reason(message(FROM, results), FROM, "mx.google.com")


def test_more_than_one_from_is_rejected():
    two_headers = f"From: x@e.example\nFrom: {FROM}\n"
    two_addresses = f"From: x@e.example, {FROM}\n"
    for from_header in (two_headers, two_addresses):
        assert "not MAIL_FROM" in reason(
            message(FROM, GOOD_RESULTS, from_header=from_header), FROM, "mx.google.com"
        )


def test_other_sender_is_rejected():
    assert "not MAIL_FROM" in reason(message("x@e.example", GOOD_RESULTS), FROM, "mx.google.com")


def test_spoofed_from_without_dmarc_is_rejected():
    results = "mx.google.com; spf=pass smtp.mailfrom=x@e.example; dmarc=fail header.from=v.example"
    assert "DMARC" in reason(message(FROM, results), FROM, "mx.google.com")


def test_dmarc_for_another_domain_is_rejected():
    results = "mx.google.com; dmarc=pass header.from=e.example"
    assert "DMARC" in reason(message(FROM, results), FROM, "mx.google.com")


def test_only_the_receiving_servers_header_counts():
    # A forged header lower down, or one from another server on top, doesn't count.
    forged = "e.example; dmarc=pass header.from=v.example"
    assert "no Authentication-Results" in reason(
        message(FROM, forged, GOOD_RESULTS), FROM, "mx.google.com"
    )
    assert "no Authentication-Results" in reason(message(FROM), FROM, "mx.google.com")
