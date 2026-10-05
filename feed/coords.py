from dataclasses import replace

from feed.parse import Block

# Job coordinates locate a customer's home as precisely as the street address. Postgres keeps them
# exact; anything served (the API, /latest.json) rounds them unless explicitly configured not to.
# 3 decimals is ~110 m: enough for a dashboard map, not enough to pick out a house.
COORD_DECIMALS = 3


def public_coords(
    latitude: float | None, longitude: float | None, exact: bool = False
) -> tuple[float | None, float | None]:
    if exact or latitude is None or longitude is None:
        return latitude, longitude
    return round(latitude, COORD_DECIMALS), round(longitude, COORD_DECIMALS)


def public_block(block: Block, exact: bool = False) -> Block:
    latitude, longitude = public_coords(block.latitude, block.longitude, exact)
    return replace(block, latitude=latitude, longitude=longitude)
