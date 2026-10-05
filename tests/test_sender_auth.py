import email

from feed.mail import sender_rejection

FROM = "reports@v.example"
GOOD_RESULTS = (
    "mx.google.com; dkim=pass header.i=@v.example;\n"
    " spf=pass smtp.mailfrom=bounce@v.example;\n dmarc=pass header.from=v.example"
)


def message(sender: str = FROM, *results: str):
    headers = "".join(f"Authentication-Results: {r}\n" for r in results)
    return email.message_from_string(f"{headers}From: Reports <{sender}>\nSubject: feed\n\nbody")


def test_authenticated_sender_is_accepted():
    assert sender_rejection(message(FROM, GOOD_RESULTS), FROM, "mx.google.com") is None


def test_other_sender_is_rejected():
    assert "not MAIL_FROM" in sender_rejection(
        message("x@e.example", GOOD_RESULTS), FROM, "mx.google.com"
    )


def test_spoofed_from_without_dmarc_is_rejected():
    results = "mx.google.com; spf=pass smtp.mailfrom=x@e.example; dmarc=fail header.from=v.example"
    assert "DMARC" in sender_rejection(message(FROM, results), FROM, "mx.google.com")


def test_dmarc_for_another_domain_is_rejected():
    results = "mx.google.com; dmarc=pass header.from=e.example"
    assert "DMARC" in sender_rejection(message(FROM, results), FROM, "mx.google.com")


def test_only_the_receiving_servers_header_counts():
    # A forged header lower down, or one from another server on top, doesn't count.
    forged = "e.example; dmarc=pass header.from=v.example"
    assert "no Authentication-Results" in sender_rejection(
        message(FROM, forged, GOOD_RESULTS), FROM, "mx.google.com"
    )
    assert "no Authentication-Results" in sender_rejection(message(FROM), FROM, "mx.google.com")
