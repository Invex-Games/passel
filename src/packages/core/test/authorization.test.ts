import {describe, expect, it} from "vitest";
import {authorizationHeader, devToken, devUserOf, isValidDevUser} from "../src/authorization.js";

describe("authorizationHeader", () => {
    it("sends a Google ID token as a Bearer token", () => {
        expect(authorizationHeader("eyJhbGciOi.payload.sig")).toBe("Bearer eyJhbGciOi.payload.sig");
        expect(devUserOf("eyJhbGciOi.payload.sig")).toBeNull();
    });

    it("sends a development token with the Dev scheme", () => {
        const token = devToken("alice");
        expect(devUserOf(token)).toBe("alice");
        expect(authorizationHeader(token)).toBe("Dev alice");
    });
});

describe("isValidDevUser", () => {
    it("accepts what the API accepts", () => {
        for (const user of ["alice", "dec.smith+test@example.com", "1234567890", "a-b_c"]) {
            expect(isValidDevUser(user), user).toBe(true);
        }
    });

    it("rejects what the API rejects", () => {
        for (const user of ["", "al ice", "alice!", "a".repeat(256)]) {
            expect(isValidDevUser(user), user).toBe(false);
        }
    });
});
