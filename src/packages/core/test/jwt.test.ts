import {describe, expect, it} from "vitest";
import {devToken} from "../src/authorization.js";
import {accountName, audienceIncludes, EXPIRY_MARGIN_SECONDS, isTokenExpired, parseJwtPayload} from "../src/jwt.js";

/** An unsigned JWT with these claims; the client never checks signatures. */
function jwt(claims: Record<string, unknown>): string {
    const segment = (value: unknown) => Buffer.from(JSON.stringify(value)).toString("base64url");
    return `${segment({alg: "none"})}.${segment(claims)}.`;
}

describe("parseJwtPayload", () => {
    it("decodes the claims, including non-ASCII ones", () => {
        expect(parseJwtPayload(jwt({sub: "1", email: "zoë@example.com", exp: 10})))
            .toEqual({sub: "1", email: "zoë@example.com", exp: 10});
    });

    it("returns null for anything that isn't a JWT with an object payload", () => {
        expect(parseJwtPayload("nope")).toBeNull();
        expect(parseJwtPayload("a.%%%.c")).toBeNull();
        expect(parseJwtPayload(`x.${Buffer.from("42").toString("base64url")}.y`)).toBeNull();
    });
});

describe("isTokenExpired", () => {
    const now = 1_000_000_000;

    it("counts a token as expired within the margin of its expiry", () => {
        const exp = now / 1000 + EXPIRY_MARGIN_SECONDS;

        expect(isTokenExpired(jwt({exp: exp + 1}), now)).toBe(false);
        expect(isTokenExpired(jwt({exp}), now)).toBe(true);
        expect(isTokenExpired(jwt({exp: now / 1000 + 10}), now, 0)).toBe(false);
    });

    it("treats a token without an expiry as expired, and a development token as never expiring", () => {
        expect(isTokenExpired(jwt({sub: "1"}), now)).toBe(true);
        expect(isTokenExpired("garbage", now)).toBe(true);
        expect(isTokenExpired(devToken("alice"), now)).toBe(false);
    });
});

describe("audienceIncludes", () => {
    it("matches a single audience or one in a list, never an empty client id", () => {
        expect(audienceIncludes("web", "web")).toBe(true);
        expect(audienceIncludes(["other", "web"], "web")).toBe(true);
        expect(audienceIncludes("other", "web")).toBe(false);
        expect(audienceIncludes(undefined, "web")).toBe(false);
        expect(audienceIncludes("", "")).toBe(false);
    });
});

describe("accountName", () => {
    it("prefers the email, then the name, and marks development users", () => {
        expect(accountName(jwt({email: "a@example.com", name: "A"}))).toBe("a@example.com");
        expect(accountName(jwt({name: "A"}))).toBe("A");
        expect(accountName(jwt({sub: "1"}))).toBeUndefined();
        expect(accountName(devToken("alice"))).toBe("alice (dev)");
    });
});
